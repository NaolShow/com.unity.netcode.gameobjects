using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using UnityEngine;
using System.Linq;
using MLAPI.Logging;
using UnityEngine.SceneManagement;
using System.IO;
using MLAPI.Configuration;
using MLAPI.Internal;
using MLAPI.Profiling;
using MLAPI.Serialization;
using MLAPI.Transports;
using BitStream = MLAPI.Serialization.BitStream;
using MLAPI.Connection;
using MLAPI.LagCompensation;
using MLAPI.Messaging;
using MLAPI.SceneManagement;
using MLAPI.Serialization.Pooled;
using MLAPI.Spawning;
using static MLAPI.Messaging.CustomMessagingManager;
using MLAPI.Exceptions;
using MLAPI.Transports.Tasks;
using MLAPI.Messaging.Buffering;
using Unity.Profiling;

namespace MLAPI
{
    /// <summary>
    /// The main component of the library
    /// </summary>
    [AddComponentMenu("MLAPI/NetworkingManager", -100)]
    public class NetworkingManager : MonoBehaviour, INetworkUpdateSystem
    {
        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
#if UNITY_2020_2_OR_NEWER
        // RuntimeAccessModifiersILPP will make this `public`
        internal static readonly Dictionary<uint, Action<NetworkedBehaviour, BitSerializer, __RpcParams>> __ntable = new Dictionary<uint, Action<NetworkedBehaviour, BitSerializer, __RpcParams>>();
#else
        [Obsolete("Please do not use, will no longer be exposed in the future versions (framework internal)")]
        public static readonly Dictionary<uint, Action<NetworkedBehaviour, BitSerializer, __RpcParams>> __ntable = new Dictionary<uint, Action<NetworkedBehaviour, BitSerializer, __RpcParams>>();
#endif

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        static ProfilerMarker s_EventTick = new ProfilerMarker("Event");
        static ProfilerMarker s_ReceiveTick = new ProfilerMarker("Receive");
        static ProfilerMarker s_SyncTime = new ProfilerMarker("SyncTime");
        static ProfilerMarker s_TransportConnect = new ProfilerMarker("TransportConnect");
        static ProfilerMarker s_HandleIncomingData = new ProfilerMarker("HandleIncomingData");
        static ProfilerMarker s_TransportDisconnect = new ProfilerMarker("TransportDisconnect");

        static ProfilerMarker s_MLAPIServerRPC = new ProfilerMarker("MLAPIServerRPC");
        static ProfilerMarker s_MLAPIServerRPCQueued = new ProfilerMarker("MLAPIServerRPCQueued");

        static ProfilerMarker s_MLAPIClientRPC = new ProfilerMarker("MLAPIClientRPC");
        static ProfilerMarker s_MLAPIClientRPCQueued = new ProfilerMarker("MLAPIClientRPCQueued");

        static ProfilerMarker s_MLAPIServerSTDRPC = new ProfilerMarker("MLAPIServerSTDRPC");
        static ProfilerMarker s_MLAPIServerSTDRPCQueued = new ProfilerMarker("MLAPIServerSTDRPCQueued");

        static ProfilerMarker s_MLAPIClientSTDRPC = new ProfilerMarker("MLAPIClientSTDRPC");
        static ProfilerMarker s_MLAPIClientSTDRPCQueued = new ProfilerMarker("MLAPIClientSTDRPCQueued");

        static ProfilerMarker s_InvokeRPC = new ProfilerMarker("InvokeRPC");
#endif
        internal RpcQueueContainer rpcQueueContainer { get; private set; }
        internal NetworkTickSystem networkTickSystem { get; private set; }

        public delegate void PerformanceDataEventHandler(PerformanceTickData profilerData);

        public static event PerformanceDataEventHandler OnPerformanceDataEvent;

        /// <summary>
        /// A synchronized time, represents the time in seconds since the server application started. Is replicated across all clients
        /// </summary>
        public float NetworkTime => Time.unscaledTime + currentNetworkTimeOffset;
        private float networkTimeOffset;
        private float currentNetworkTimeOffset;
        /// <summary>
        /// Gets or sets if the NetworkingManager should be marked as DontDestroyOnLoad
        /// </summary>
        [HideInInspector]
        public bool DontDestroy = true;
        /// <summary>
        /// Gets or sets if the application should be set to run in background
        /// </summary>
        [HideInInspector]
        public bool RunInBackground = true;
        /// <summary>
        /// The log level to use
        /// </summary>
        [HideInInspector]
        public LogLevel LogLevel = LogLevel.Normal;
        /// <summary>
        /// The singleton instance of the NetworkingManager
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use Singleton instead", false)]
        public static NetworkingManager singleton => Singleton;
        /// <summary>
        /// The singleton instance of the NetworkingManager
        /// </summary>
        public static NetworkingManager Singleton { get; private set; }
        /// <summary>
        /// Gets the networkId of the server
        /// </summary>
		public ulong ServerClientId => NetworkConfig.NetworkTransport != null ? NetworkConfig.NetworkTransport.ServerClientId : throw new NullReferenceException("The transport in the active NetworkConfig is null");
        /// <summary>
        /// The clientId the server calls the local client by, only valid for clients
        /// </summary>
        public ulong LocalClientId
        {
            get => IsServer ? NetworkConfig.NetworkTransport.ServerClientId : localClientId;
            internal set => localClientId = value;
        }
        private ulong localClientId;
        /// <summary>
        /// Gets a dictionary of connected clients and their clientId keys. This is only populated on the server.
        /// </summary>
        public readonly Dictionary<ulong, NetworkedClient> ConnectedClients = new Dictionary<ulong, NetworkedClient>();
        /// <summary>
        /// Gets a list of connected clients. This is only populated on the server.
        /// </summary>
        public readonly List<NetworkedClient> ConnectedClientsList = new List<NetworkedClient>();
        /// <summary>
        /// Gets a dictionary of the clients that have been accepted by the transport but are still pending by the MLAPI. This is only populated on the server.
        /// </summary>
        public readonly Dictionary<ulong, PendingClient> PendingClients = new Dictionary<ulong, PendingClient>();
        /// <summary>
        /// Gets Whether or not a server is running
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use IsServer instead", false)]
        public bool isServer => IsServer;
        /// <summary>
        /// Gets Whether or not a server is running
        /// </summary>
        public bool IsServer { get; internal set; }
        /// <summary>
        /// Gets Whether or not a client is running
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use IsClient instead", false)]
        public bool isClient => IsClient;
        /// <summary>
        /// Gets Whether or not a client is running
        /// </summary>
        public bool IsClient { get; internal set; }
        /// <summary>
        /// Gets if we are running as host
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use IsHost instead", false)]
        public bool isHost => IsHost;
        /// <summary>
        /// Gets if we are running as host
        /// </summary>
        public bool IsHost => IsServer && IsClient;
        /// <summary>
        /// Gets Whether or not we are listening for connections
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use IsListening instead", false)]
        public bool isListening => IsListening;
        /// <summary>
        /// Gets Whether or not we are listening for connections
        /// </summary>
        public bool IsListening { get; internal set; }
        /// <summary>
        /// Gets if we are connected as a client
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use IsConnectedClient instead", false)]
        public bool isConnectedClients => IsConnectedClient;
        /// <summary>
        /// Gets if we are connected as a client
        /// </summary>
        public bool IsConnectedClient { get; internal set; }
        /// <summary>
        /// The callback to invoke once a client connects. This callback is only ran on the server and on the local client that connects.
        /// </summary>
        public event Action<ulong> OnClientConnectedCallback = null;
        internal void InvokeOnClientConnectedCallback(ulong clientId)
        {
            if (OnClientConnectedCallback != null)
            {
                OnClientConnectedCallback(clientId);
            }
        }
        /// <summary>
        /// The callback to invoke when a client disconnects. This callback is only ran on the server and on the local client that disconnects.
        /// </summary>
        public event Action<ulong> OnClientDisconnectCallback = null;
        internal void InvokeOnClientDisconnectCallback(ulong clientId)
        {
            if (OnClientDisconnectCallback != null)
            {
                OnClientDisconnectCallback(clientId);
            }
        }
        /// <summary>
        /// The callback to invoke once the server is ready
        /// </summary>
        public event Action OnServerStarted = null;
        /// <summary>
        /// Delegate type called when connection has been approved. This only has to be set on the server.
        /// </summary>
        /// <param name="createPlayerObject">If true, a player object will be created. Otherwise the client will have no object.</param>
        /// <param name="playerPrefabHash">The prefabHash to use for the client. If createPlayerObject is false, this is ignored. If playerPrefabHash is null, the default player prefab is used.</param>
        /// <param name="approved">Whether or not the client was approved</param>
        /// <param name="position">The position to spawn the client at. If null, the prefab position is used.</param>
        /// <param name="rotation">The rotation to spawn the client with. If null, the prefab position is used.</param>
        public delegate void ConnectionApprovedDelegate(bool createPlayerObject, ulong? playerPrefabHash, bool approved, Vector3? position, Quaternion? rotation);
        /// <summary>
        /// The callback to invoke during connection approval
        /// </summary>
        public event Action<byte[], ulong, ConnectionApprovedDelegate> ConnectionApprovalCallback = null;
        internal void InvokeConnectionApproval(byte[] payload, ulong clientId, ConnectionApprovedDelegate action)
        {
            if (ConnectionApprovalCallback != null)
            {
                ConnectionApprovalCallback(payload, clientId, action);
            }
        }
        /// <summary>
        /// The current NetworkingConfiguration
        /// </summary>
        [HideInInspector]
        public NetworkConfig NetworkConfig;
        [Obsolete("Use OnUnnamedMessage instead")]
        public event UnnamedMessageDelegate OnIncomingCustomMessage;
        /// <summary>
        /// The current hostname we are connected to, used to validate certificate
        /// </summary>
        public string ConnectedHostname { get; private set; }
        internal static event Action OnSingletonReady;

        internal void InvokeOnIncomingCustomMessage(ulong clientId, Stream stream)
        {
            if (OnIncomingCustomMessage != null)
            {
                OnIncomingCustomMessage(clientId, stream);
            }
        }

        /// <summary>
        /// Sends unnamed message to a list of clients
        /// </summary>
        /// <param name="clientIds">The clients to send to, sends to everyone if null</param>
        /// <param name="stream">The message stream containing the data</param>
        /// <param name="channel">The channel to send the data on</param>
        [Obsolete("Use CustomMessagingManager.SendUnnamedMessage instead")]
        public void SendCustomMessage(List<ulong> clientIds, BitStream stream, Channel channel = Channel.Internal)
        {
            if (!IsServer)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Error) NetworkLog.LogWarning("Can not send unnamed message to multiple users as a client");
                return;
            }

            InternalMessageSender.Send(MLAPIConstants.MLAPI_UNNAMED_MESSAGE, channel, clientIds, stream);
        }

        /// <summary>
        /// Sends a unnamed message to a specific client
        /// </summary>
        /// <param name="clientId">The client to send the message to</param>
        /// <param name="stream">The message stream containing the data</param>
        /// <param name="channel">The channel tos end the data on</param>
        [Obsolete("Use CustomMessagingManager.SendUnnamedMessage instead")]
        public void SendCustomMessage(ulong clientId, BitStream stream, Channel channel = Channel.Internal)
        {
            InternalMessageSender.Send(clientId, MLAPIConstants.MLAPI_UNNAMED_MESSAGE, channel, stream);
        }

        private void OnValidate()
        {
            if (NetworkConfig == null)
                return; //May occur when the component is added

            if (GetComponentInChildren<NetworkedObject>() != null)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("The NetworkingManager cannot be a NetworkedObject. This will lead to weird side effects.");
            }

            if (!NetworkConfig.RegisteredScenes.Contains(SceneManager.GetActiveScene().name))
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("The active scene is not registered as a networked scene. The MLAPI has added it");
                NetworkConfig.RegisteredScenes.Add(SceneManager.GetActiveScene().name);
            }

            for (int i = 0; i < NetworkConfig.NetworkedPrefabs.Count; i++)
            {
                if (NetworkConfig.NetworkedPrefabs[i] != null && NetworkConfig.NetworkedPrefabs[i].Prefab != null)
                {
                    if (NetworkConfig.NetworkedPrefabs[i].Prefab.GetComponent<NetworkedObject>() == null)
                    {
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("The network prefab [" + i + "] does not have a NetworkedObject component");
                    }
                    else
                    {
                        NetworkConfig.NetworkedPrefabs[i].Prefab.GetComponent<NetworkedObject>().ValidateHash();
                    }
                }
            }

            // TODO: Show which two prefab generators that collide
            HashSet<ulong> hashes = new HashSet<ulong>();

            for (int i = 0; i < NetworkConfig.NetworkedPrefabs.Count; i++)
            {
                if (hashes.Contains(NetworkConfig.NetworkedPrefabs[i].Hash))
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                    {
                        var prefabHashGenerator = NetworkConfig.NetworkedPrefabs[i].Prefab.GetComponent<NetworkedObject>().PrefabHashGenerator;
                        NetworkLog.LogError($"PrefabHash collision! You have two prefabs with the same hash (PrefabHashGenerator = {prefabHashGenerator}). This is not supported");
                    }

                }

                hashes.Add(NetworkConfig.NetworkedPrefabs[i].Hash);
            }

            int playerPrefabCount = NetworkConfig.NetworkedPrefabs.Count(x => x.PlayerPrefab == true);

            if (playerPrefabCount == 0 && !NetworkConfig.ConnectionApproval && NetworkConfig.CreatePlayerPrefab)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("There is no NetworkedPrefab marked as a PlayerPrefab");
            }
            else if (playerPrefabCount > 1)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Only one networked prefab can be marked as a player prefab");
            }

            NetworkedPrefab prefab = NetworkConfig.NetworkedPrefabs.FirstOrDefault(x => x.PlayerPrefab == true);

            if (prefab == null)
            {
                NetworkConfig.PlayerPrefabHash = null;
            }
            else
            {
                if (NetworkConfig.PlayerPrefabHash == null)
                {
                    NetworkConfig.PlayerPrefabHash = new NullableBoolSerializable();
                }
                NetworkConfig.PlayerPrefabHash.Value = prefab.Hash;
            }
        }

        private void Init(bool server)
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("Init()");

            LocalClientId = 0;
            networkTimeOffset = 0f;
            currentNetworkTimeOffset = 0f;
            m_LastReceiveTickTime = 0f;
            m_LastReceiveTickTime = 0f;
            m_EventOvershootCounter = 0f;
            PendingClients.Clear();
            ConnectedClients.Clear();
            ConnectedClientsList.Clear();

            SpawnManager.SpawnedObjects.Clear();
            SpawnManager.SpawnedObjectsList.Clear();
            SpawnManager.releasedNetworkObjectIds.Clear();
            SpawnManager.pendingSoftSyncObjects.Clear();
            NetworkSceneManager.registeredSceneNames.Clear();
            NetworkSceneManager.sceneIndexToString.Clear();
            NetworkSceneManager.sceneNameToIndex.Clear();
            NetworkSceneManager.sceneSwitchProgresses.Clear();

            if (NetworkConfig.NetworkTransport == null)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Error) NetworkLog.LogError("No transport has been selected!");
                return;
            }

            //This 'if' should never enter
            if (networkTickSystem != null)
            {
                networkTickSystem.Dispose();
            }
            networkTickSystem = new NetworkTickSystem();

            //This should never happen, but in the event that it does there should be (at a minimum) a unity error logged.
            if(rpcQueueContainer != null)
            {
                UnityEngine.Debug.LogError("Init was invoked, but rpcQueueContainer was already initialized! (destroying previous instance)");
                rpcQueueContainer.Shutdown();
                rpcQueueContainer = null;
            }

            //The RpcQueueContainer must be initialized within the Init method ONLY
            //It should ONLY be shutdown and destroyed in the Shutdown method (other than just above)
            rpcQueueContainer = new RpcQueueContainer(false);

            //Note: Since frame history is not being used, this is set to 0
            //To test frame history, increase the number to (n) where n > 0
            rpcQueueContainer.Initialize(0);

            // Register INetworkUpdateSystem (always register this after rpcQueueContainer has been instantiated)
            this.RegisterNetworkUpdate(NetworkUpdateStage.EarlyUpdate);
            this.RegisterNetworkUpdate(NetworkUpdateStage.PreUpdate);

            if (NetworkConfig.EnableSceneManagement)
            {
                NetworkConfig.RegisteredScenes.Sort(StringComparer.Ordinal);

                for (int i = 0; i < NetworkConfig.RegisteredScenes.Count; i++)
                {
                    NetworkSceneManager.registeredSceneNames.Add(NetworkConfig.RegisteredScenes[i]);
                    NetworkSceneManager.sceneIndexToString.Add((uint)i, NetworkConfig.RegisteredScenes[i]);
                    NetworkSceneManager.sceneNameToIndex.Add(NetworkConfig.RegisteredScenes[i], (uint)i);
                }

                NetworkSceneManager.SetCurrentSceneIndex();
            }

            for (int i = 0; i < NetworkConfig.NetworkedPrefabs.Count; i++)
            {
                if (NetworkConfig.NetworkedPrefabs[i] == null || NetworkConfig.NetworkedPrefabs[i].Prefab == null)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Error) NetworkLog.LogError("Networked prefab cannot be null");
                }
                else if (NetworkConfig.NetworkedPrefabs[i].Prefab.GetComponent<NetworkedObject>() == null)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Error) NetworkLog.LogError("Networked prefab is missing a NetworkedObject component");
                }
                else
                {
                    NetworkConfig.NetworkedPrefabs[i].Prefab.GetComponent<NetworkedObject>().ValidateHash();
                }
            }

            NetworkConfig.NetworkTransport.OnTransportEvent += HandleRawTransportPoll;

            NetworkConfig.NetworkTransport.ResetChannelCache();

            NetworkConfig.NetworkTransport.Init();
        }

        /// <summary>
        /// Starts a server
        /// </summary>
        public SocketTasks StartServer()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("StartServer()");
            if (IsServer || IsClient)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Cannot start server while an instance is already running");
                return SocketTask.Fault.AsTasks();
            }

            if (NetworkConfig.ConnectionApproval)
            {
                if (ConnectionApprovalCallback == null)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("No ConnectionApproval callback defined. Connection approval will timeout");
                }
            }

            Init(true);

            SocketTasks tasks = NetworkConfig.NetworkTransport.StartServer();

            IsServer = true;
            IsClient = false;
            IsListening = true;

            SpawnManager.ServerSpawnSceneObjectsOnStartSweep();

            if (OnServerStarted != null)
                OnServerStarted.Invoke();

            return tasks;
        }

        /// <summary>
        /// Starts a client
        /// </summary>
        public SocketTasks StartClient()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("StartClient()");

            if (IsServer || IsClient)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Cannot start client while an instance is already running");
                return SocketTask.Fault.AsTasks();
            }

            Init(false);

            SocketTasks tasks = NetworkConfig.NetworkTransport.StartClient();

            IsServer = false;
            IsClient = true;
            IsListening = true;

            return tasks;
        }

        /// <summary>
        /// Stops the running server
        /// </summary>
        public void StopServer()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("StopServer()");
            HashSet<ulong> disconnectedIds = new HashSet<ulong>();
            //Don't know if I have to disconnect the clients. I'm assuming the NetworkTransport does all the cleaning on shtudown. But this way the clients get a disconnect message from server (so long it does't get lost)

            foreach (KeyValuePair<ulong, NetworkedClient> pair in ConnectedClients)
            {
                if (!disconnectedIds.Contains(pair.Key))
                {
                    disconnectedIds.Add(pair.Key);

					if (pair.Key == NetworkConfig.NetworkTransport.ServerClientId)
                        continue;

                    NetworkConfig.NetworkTransport.DisconnectRemoteClient(pair.Key);
                }
            }

            foreach (KeyValuePair<ulong, PendingClient> pair in PendingClients)
            {
                if(!disconnectedIds.Contains(pair.Key))
                {
                    disconnectedIds.Add(pair.Key);
                    if (pair.Key == NetworkConfig.NetworkTransport.ServerClientId)
                        continue;

                    NetworkConfig.NetworkTransport.DisconnectRemoteClient(pair.Key);
                }
            }

            IsServer = false;
            Shutdown();
        }

        /// <summary>
        /// Stops the running host
        /// </summary>
        public void StopHost()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("StopHost()");
            IsServer = false;
            IsClient = false;
            StopServer();
            //We don't stop client since we dont actually have a transport connection to our own host. We just handle host messages directly in the MLAPI
        }

        /// <summary>
        /// Stops the running client
        /// </summary>
        public void StopClient()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("StopClient()");
            IsClient = false;
            NetworkConfig.NetworkTransport.DisconnectLocalClient();
            IsConnectedClient = false;
            Shutdown();
        }

        /// <summary>
        /// Starts a Host
        /// </summary>
        public SocketTasks StartHost(Vector3? position = null, Quaternion? rotation = null, bool? createPlayerObject = null, ulong? prefabHash = null, Stream payloadStream = null)
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("StartHost()");

            if (IsServer || IsClient)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Cannot start host while an instance is already running");
                return SocketTask.Fault.AsTasks();
            }

            if (NetworkConfig.ConnectionApproval)
            {
                if (ConnectionApprovalCallback == null)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("No ConnectionApproval callback defined. Connection approval will timeout");
                }
            }

            Init(true);

            SocketTasks tasks = NetworkConfig.NetworkTransport.StartServer();

            IsServer = true;
            IsClient = true;
            IsListening = true;

            ulong hostClientId = NetworkConfig.NetworkTransport.ServerClientId;

            ConnectedClients.Add(hostClientId, new NetworkedClient()
            {
                ClientId = hostClientId
            });

            ConnectedClientsList.Add(ConnectedClients[hostClientId]);

            if ((createPlayerObject == null && NetworkConfig.CreatePlayerPrefab) || (createPlayerObject != null && createPlayerObject.Value))
            {
                NetworkedObject netObject = SpawnManager.CreateLocalNetworkedObject(false, 0, (prefabHash == null ? NetworkConfig.PlayerPrefabHash.Value : prefabHash.Value), null, position, rotation);
                SpawnManager.SpawnNetworkedObjectLocally(netObject, SpawnManager.GetNetworkObjectId(), false, true, hostClientId, payloadStream, payloadStream != null, payloadStream == null ? 0 : (int)payloadStream.Length, false, false);

                if (netObject.CheckObjectVisibility == null || netObject.CheckObjectVisibility(hostClientId))
                {
                    netObject.observers.Add(hostClientId);
                }
            }

            SpawnManager.ServerSpawnSceneObjectsOnStartSweep();

            if (OnServerStarted != null)
                OnServerStarted.Invoke();

            return tasks;
        }

        public void SetSingleton()
        {
            Singleton = this;

            if (OnSingletonReady != null)
                OnSingletonReady();
        }

        private void OnEnable()
        {
            if (Singleton != null && Singleton != this)
            {
                Destroy(gameObject);
                return;
            }

            SetSingleton();
            if (DontDestroy)
                DontDestroyOnLoad(gameObject);
            if (RunInBackground)
                Application.runInBackground = true;
        }

        private void OnDestroy()
        {
            if (Singleton != null && Singleton == this)
            {
                Shutdown();
                Singleton = null;
            }
        }

        public void Shutdown()
        {
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("Shutdown()");

            // Unregister INetworkUpdateSystem before shutting down the RpcQueueContainer
            this.UnregisterAllNetworkUpdates();

            //If an instance of the RpcQueueContainer is still around, then shut it down and remove the reference
            if (rpcQueueContainer != null)
            {
                rpcQueueContainer.Shutdown();
                rpcQueueContainer = null;
            }

            if (networkTickSystem != null)
            {
                networkTickSystem.Dispose();
            }
            networkTickSystem = null;

            NetworkProfiler.Stop();
            IsListening = false;
            IsServer = false;
            IsClient = false;
            NetworkConfig.NetworkTransport.OnTransportEvent -= HandleRawTransportPoll;
            SpawnManager.DestroyNonSceneObjects();
            SpawnManager.ServerResetShudownStateForSceneObjects();

            //The Transport is set during Init time, thus it is possible for the Transport to be null
            if (NetworkConfig != null && NetworkConfig.NetworkTransport != null)
                NetworkConfig.NetworkTransport.Shutdown();


        }

        // INetworkUpdateSystem
        public void NetworkUpdate(NetworkUpdateStage updateStage)
        {
            switch (updateStage)
            {
                case NetworkUpdateStage.EarlyUpdate:
                    OnNetworkEarlyUpdate();
                    break;
                case NetworkUpdateStage.PreUpdate:
                    OnNetworkPreUpdate();
                    break;
            }
        }

        private float m_LastReceiveTickTime;
        private float m_LastEventTickTime;
        private float m_EventOvershootCounter;
        private float m_LastTimeSyncTime;

        private void OnNetworkEarlyUpdate()
        {
            PerformanceDataManager.BeginNewTick();
            if (NetworkConfig.NetworkTransport is ITransportProfilerData profileTransport)
            {
                profileTransport.BeginNewTick();
            }

            if (IsListening)
            {
                // Process received data
                if ((NetworkTime - m_LastReceiveTickTime >= (1f / NetworkConfig.ReceiveTickrate)) || NetworkConfig.ReceiveTickrate <= 0)
                {
                    PerformanceDataManager.Increment(ProfilerConstants.ReceiveTickRate);
                    ProfilerStatManager.rcvTickRate.Record();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_ReceiveTick.Begin();
#endif
                    var IsLoopBack = false;

                    NetworkProfiler.StartTick(TickType.Receive);

                    //If we are in loopback mode, we don't need to touch the transport
                    if (!IsLoopBack)
                    {
                        NetEventType eventType;
                        int processedEvents = 0;
                        do
                        {
                            processedEvents++;
                            eventType = NetworkConfig.NetworkTransport.PollEvent(out ulong clientId, out Channel channel, out ArraySegment<byte> payload, out float receiveTime);
                            HandleRawTransportPoll(eventType, clientId, channel, payload, receiveTime);

                            // Only do another iteration if: there are no more messages AND (there is no limit to max events or we have processed less than the maximum)
                        } while (IsListening && (eventType != NetEventType.Nothing) && (NetworkConfig.MaxReceiveEventsPerTickRate <= 0 || processedEvents < NetworkConfig.MaxReceiveEventsPerTickRate));
                    }

                    m_LastReceiveTickTime = NetworkTime;

                    NetworkProfiler.EndTick();

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_ReceiveTick.End();
#endif
                }
            }
        }

        private void OnNetworkPreUpdate()
        {
            if (IsListening)
            {
                if (((NetworkTime - m_LastEventTickTime >= (1f / NetworkConfig.EventTickrate))))
                {

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_EventTick.Begin();
#endif
#if UNITY_EDITOR
                    NetworkProfiler.StartTick(TickType.Event);
#endif

                    if (IsServer)
                    {
                        m_EventOvershootCounter += ((NetworkTime - m_LastEventTickTime) - (1f / NetworkConfig.EventTickrate));
                        LagCompensationManager.AddFrames();
                    }

                    if (NetworkConfig.EnableNetworkedVar)
                    {
                        // Do NetworkedVar updates
                        NetworkedBehaviour.NetworkedBehaviourUpdate();
                    }

                    if (!IsServer && NetworkConfig.EnableMessageBuffering)
                    {
                        BufferManager.CleanBuffer();
                    }

                    if (IsServer)
                    {
                        m_LastEventTickTime = NetworkTime;
                    }
#if UNITY_EDITOR
                    NetworkProfiler.EndTick();
#endif

#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_EventTick.End();
#endif
                }
                else if (IsServer && m_EventOvershootCounter >= ((1f / NetworkConfig.EventTickrate)))
                {
#if UNITY_EDITOR
                    NetworkProfiler.StartTick(TickType.Event);
#endif
                    //We run this one to compensate for previous update overshoots.
                    m_EventOvershootCounter -= (1f / NetworkConfig.EventTickrate);
                    LagCompensationManager.AddFrames();
#if UNITY_EDITOR
                    NetworkProfiler.EndTick();
#endif
                }

                if (IsServer && NetworkConfig.EnableTimeResync && NetworkTime - m_LastTimeSyncTime >= NetworkConfig.TimeResyncInterval)
                {
#if UNITY_EDITOR
                    NetworkProfiler.StartTick(TickType.Event);
#endif
                    SyncTime();
                    m_LastTimeSyncTime = NetworkTime;
#if UNITY_EDITOR
                    NetworkProfiler.EndTick();
#endif
                }

                if (!Mathf.Approximately(networkTimeOffset, currentNetworkTimeOffset)) {
                    // Smear network time adjustments by no more than 200ms per second.  This should help code deal with
                    // changes more gracefully, since the network time will always flow forward at a reasonable pace.
                    float maxDelta = Mathf.Max(0.001f, 0.2f * Time.unscaledDeltaTime);
                    currentNetworkTimeOffset += Mathf.Clamp(networkTimeOffset - currentNetworkTimeOffset, -maxDelta, maxDelta);
                }
            }

            if(NetworkConfig.NetworkTransport is ITransportProfilerData profileTransport)
            {
                var transportProfilerData = profileTransport.GetTransportProfilerData();
                PerformanceDataManager.AddTransportData(transportProfilerData);
            }

            OnPerformanceDataEvent?.Invoke(PerformanceDataManager.GetData());
        }

        internal void UpdateNetworkTime(ulong clientId, float netTime, float receiveTime, bool warp = false)
        {
            float rtt = NetworkConfig.NetworkTransport.GetCurrentRtt(clientId) / 1000f;
            networkTimeOffset = netTime - receiveTime + rtt / 2f;
            if (warp) {
                currentNetworkTimeOffset = networkTimeOffset;
            }
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo($"Received network time {netTime}, RTT to server is {rtt}, {(warp ? "setting" : "smearing")} offset to {networkTimeOffset} (delta {networkTimeOffset - currentNetworkTimeOffset})");
        }

        internal void SendConnectionRequest()
        {
            using (PooledBitStream stream = PooledBitStream.Get())
            {
                using (PooledBitWriter writer = PooledBitWriter.Get(stream))
                {
                    writer.WriteUInt64Packed(NetworkConfig.GetConfig());

                    if (NetworkConfig.ConnectionApproval)
                        writer.WriteByteArray(NetworkConfig.ConnectionData);
                }

                InternalMessageSender.Send(ServerClientId, MLAPIConstants.MLAPI_CONNECTION_REQUEST, Channel.Internal, stream);
            }
        }

        private IEnumerator ApprovalTimeout(ulong clientId)
        {
            float timeStarted = NetworkTime;
            //We yield every frame incase a pending client disconnects and someone else gets its connection id
            while (NetworkTime - timeStarted < NetworkConfig.ClientConnectionBufferTimeout && PendingClients.ContainsKey(clientId))
            {
                yield return null;
            }

            if (PendingClients.ContainsKey(clientId) && !ConnectedClients.ContainsKey(clientId))
            {
                // Timeout
                if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("Client " + clientId + " Handshake Timed Out");
                DisconnectClient(clientId);
            }
        }

        internal IEnumerator TimeOutSwitchSceneProgress(SceneSwitchProgress switchSceneProgress)
        {
            yield return new WaitForSecondsRealtime(NetworkConfig.LoadSceneTimeOut);
            switchSceneProgress.SetTimedOut();
        }

        private void HandleRawTransportPoll(NetEventType eventType, ulong clientId, Channel channel, ArraySegment<byte> payload, float receiveTime)
        {
            PerformanceDataManager.Increment(ProfilerConstants.NumberBytesReceived, payload.Count);
            ProfilerStatManager.bytesRcvd.Record(payload.Count);
            switch (eventType)
            {
                case NetEventType.Connect:
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_TransportConnect.Begin();
#endif
                    NetworkProfiler.StartEvent(TickType.Receive, (uint)payload.Count, channel, "TRANSPORT_CONNECT");
                    if (IsServer)
                    {
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("Client Connected");

                        PendingClients.Add(clientId, new PendingClient()
                        {
                            ClientId = clientId,
                            ConnectionState = PendingClient.State.PendingConnection
                        });
                        StartCoroutine(ApprovalTimeout(clientId));
                    }
                    else
                    {
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("Connected");

                        SendConnectionRequest();
                        StartCoroutine(ApprovalTimeout(clientId));
                    }
                    NetworkProfiler.EndEvent();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_TransportConnect.End();
#endif
                    break;
                case NetEventType.Data:
                    {
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo($"Incoming Data From {clientId} : {payload.Count} bytes");
                        HandleIncomingData(clientId, channel, payload, receiveTime, true);
                        break;
                    }
                case NetEventType.Disconnect:
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_TransportDisconnect.Begin();
#endif
                    NetworkProfiler.StartEvent(TickType.Receive, 0, Channel.Internal, "TRANSPORT_DISCONNECT");

                    if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("Disconnect Event From " + clientId);

                    if (IsServer)
                        OnClientDisconnectFromServer(clientId);
                    else
                    {
                        IsConnectedClient = false;
                        StopClient();
                    }

                    if (OnClientDisconnectCallback != null)
                        OnClientDisconnectCallback.Invoke(clientId);
                    NetworkProfiler.EndEvent();
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                    s_TransportDisconnect.End();
#endif
                    break;
            }
        }

        private readonly BitStream m_InputStreamWrapper = new BitStream(new byte[0]);
        private readonly RpcBatcher m_RpcBatcher = new RpcBatcher();

        internal void HandleIncomingData(ulong clientId, Channel channel, ArraySegment<byte> data, float receiveTime, bool allowBuffer)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleIncomingData.Begin();
#endif
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("Unwrapping Data Header");

            m_InputStreamWrapper.SetTarget(data.Array);
            m_InputStreamWrapper.SetLength(data.Count + data.Offset);
            m_InputStreamWrapper.Position = data.Offset;

            using (var messageStream = MessagePacker.UnwrapMessage(m_InputStreamWrapper, out byte messageType))
            {
                if (messageStream == null)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Error) NetworkLog.LogError("Message unwrap could not be completed. Was the header corrupt? Crypto error?");
                    return;
                }

                if (messageType == MLAPIConstants.INVALID)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Error) NetworkLog.LogError("Message unwrap read an invalid messageType");
                    return;
                }

                uint headerByteSize = (uint)Arithmetic.VarIntSize(messageType);
                NetworkProfiler.StartEvent(TickType.Receive, (uint)(data.Count - headerByteSize), channel, messageType);

                if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("Data Header: messageType=" + messageType);

                // Client tried to send a network message that was not the connection request before he was accepted.
                if (PendingClients.ContainsKey(clientId) && PendingClients[clientId].ConnectionState == PendingClient.State.PendingConnection && messageType != MLAPIConstants.MLAPI_CONNECTION_REQUEST)
                {
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Normal) NetworkLog.LogWarning("Message received from clientId " + clientId + " before it has been accepted");
                    return;
                }

                #region INTERNAL MESSAGE

                switch (messageType)
                {
                    case MLAPIConstants.MLAPI_CONNECTION_REQUEST:
                        if (IsServer) InternalMessageHandler.HandleConnectionRequest(clientId, messageStream);
                        break;
                    case MLAPIConstants.MLAPI_CONNECTION_APPROVED:
                        if (IsClient) InternalMessageHandler.HandleConnectionApproved(clientId, messageStream, receiveTime);
                        break;
                    case MLAPIConstants.MLAPI_ADD_OBJECT:
                        if (IsClient) InternalMessageHandler.HandleAddObject(clientId, messageStream);
                        break;
                    case MLAPIConstants.MLAPI_DESTROY_OBJECT:
                        if (IsClient) InternalMessageHandler.HandleDestroyObject(clientId, messageStream);
                        break;
                    case MLAPIConstants.MLAPI_SWITCH_SCENE:
                        if (IsClient) InternalMessageHandler.HandleSwitchScene(clientId, messageStream);
                        break;
                    case MLAPIConstants.MLAPI_CHANGE_OWNER:
                        if (IsClient) InternalMessageHandler.HandleChangeOwner(clientId, messageStream);
                        break;
                    case MLAPIConstants.MLAPI_ADD_OBJECTS:
                        if (IsClient) InternalMessageHandler.HandleAddObjects(clientId, messageStream);
                        break;
                    case MLAPIConstants.MLAPI_DESTROY_OBJECTS:
                        if (IsClient) InternalMessageHandler.HandleDestroyObjects(clientId, messageStream);
                        break;
                    case MLAPIConstants.MLAPI_TIME_SYNC:
                        if (IsClient) InternalMessageHandler.HandleTimeSync(clientId, messageStream, receiveTime);
                        break;
                    case MLAPIConstants.MLAPI_NETWORKED_VAR_DELTA:
                        InternalMessageHandler.HandleNetworkedVarDelta(clientId, messageStream, BufferCallback, new PreBufferPreset()
                        {
                            AllowBuffer = allowBuffer,
                            Channel = channel,
                            ClientId = clientId,
                            Data = data,
                            MessageType = messageType,
                            ReceiveTime = receiveTime
                        });
                        break;
                    case MLAPIConstants.MLAPI_NETWORKED_VAR_UPDATE:
                        InternalMessageHandler.HandleNetworkedVarUpdate(clientId, messageStream, BufferCallback, new PreBufferPreset()
                        {
                            AllowBuffer = allowBuffer,
                            Channel = channel,
                            ClientId = clientId,
                            Data = data,
                            MessageType = messageType,
                            ReceiveTime = receiveTime
                        });
                        break;
                    case MLAPIConstants.MLAPI_UNNAMED_MESSAGE:
                        InternalMessageHandler.HandleUnnamedMessage(clientId, messageStream);
                        break;
                    case MLAPIConstants.MLAPI_NAMED_MESSAGE:
                        InternalMessageHandler.HandleNamedMessage(clientId, messageStream);
                        break;
                    case MLAPIConstants.MLAPI_CLIENT_SWITCH_SCENE_COMPLETED:
                        if (IsServer && NetworkConfig.EnableSceneManagement) InternalMessageHandler.HandleClientSwitchSceneCompleted(clientId, messageStream);
                        break;
                    case MLAPIConstants.MLAPI_SERVER_LOG:
                        if (IsServer && NetworkConfig.EnableNetworkLogs) InternalMessageHandler.HandleNetworkLog(clientId, messageStream);
                        break;
                    case MLAPIConstants.MLAPI_SERVER_RPC:
                        {
                            if (IsServer)
                            {
                                if(rpcQueueContainer.IsUsingBatching())
                                {
                                    m_RpcBatcher.ReceiveItems(messageStream, ReceiveCallback, RpcQueueContainer.QueueItemType.ServerRpc, clientId, receiveTime);
                                    ProfilerStatManager.rpcBatchesRcvd.Record();
                                    PerformanceDataManager.Increment(ProfilerConstants.NumberOfRPCBatchesReceived);
                                }
                                else
                                {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                                    s_MLAPIServerSTDRPCQueued.Begin();
#endif
                                    InternalMessageHandler.RPCReceiveQueueItem(clientId, messageStream, receiveTime,RpcQueueContainer.QueueItemType.ServerRpc);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                                    s_MLAPIServerSTDRPCQueued.End();
#endif
                                }
                            }
                            break;
                        }
                    case MLAPIConstants.MLAPI_CLIENT_RPC:
                        {
                            if (IsClient)
                            {
                                if(rpcQueueContainer.IsUsingBatching())
                                {
                                    m_RpcBatcher.ReceiveItems(messageStream, ReceiveCallback, RpcQueueContainer.QueueItemType.ClientRpc, clientId, receiveTime);
                                    ProfilerStatManager.rpcBatchesRcvd.Record();
                                    PerformanceDataManager.Increment(ProfilerConstants.NumberOfRPCBatchesReceived);
                                }
                                else
                                {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                                    s_MLAPIClientSTDRPCQueued.Begin();
#endif
                                    InternalMessageHandler.RPCReceiveQueueItem(clientId, messageStream,receiveTime,RpcQueueContainer.QueueItemType.ClientRpc);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                                    s_MLAPIClientSTDRPCQueued.End();
#endif
                                }
                            }

                            break;
                        }
                    default:
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Error) NetworkLog.LogError("Read unrecognized messageType " + messageType);
                        break;
                }
                #endregion

                NetworkProfiler.EndEvent();
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_HandleIncomingData.End();
#endif
        }

        private static void ReceiveCallback(BitStream messageStream, RpcQueueContainer.QueueItemType messageType, ulong clientId, float receiveTime)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (messageType == RpcQueueContainer.QueueItemType.ServerRpc)
            {
                s_MLAPIServerSTDRPCQueued.Begin();
            }
            else
            {
                s_MLAPIClientSTDRPCQueued.Begin();
            }
#endif
            InternalMessageHandler.RPCReceiveQueueItem(clientId, messageStream, receiveTime, messageType);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (messageType == RpcQueueContainer.QueueItemType.ServerRpc)
            {
                s_MLAPIServerSTDRPCQueued.End();
            }
            else
            {
                s_MLAPIClientSTDRPCQueued.End();
            }
#endif
        }

        /// <summary>
        /// InvokeRPC
        /// Called when an inbound queued RPC is invoked
        /// </summary>
        /// <param name="queueItem">frame queue item to invoke</param>
#pragma warning disable 618
        internal static void InvokeRpc(RpcFrameQueueItem queueItem)
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_InvokeRPC.Begin();
#endif
            var networkObjectId = queueItem.streamReader.ReadUInt64Packed();
            var networkBehaviourId = queueItem.streamReader.ReadUInt16Packed();
            var networkUpdateStage = queueItem.streamReader.ReadByteDirect();
            var networkMethodId = queueItem.streamReader.ReadUInt32Packed();

            if (__ntable.ContainsKey(networkMethodId))
            {
                if (!SpawnManager.SpawnedObjects.ContainsKey(networkObjectId)) return;
                var networkObject = SpawnManager.SpawnedObjects[networkObjectId];

                var networkBehaviour = networkObject.GetBehaviourAtOrderIndex(networkBehaviourId);
                if (ReferenceEquals(networkBehaviour, null)) return;

                var rpcParams = new __RpcParams();
                switch (queueItem.queueItemType)
                {
                    case RpcQueueContainer.QueueItemType.ServerRpc:
                        rpcParams.Server = new ServerRpcParams
                        {
                            Receive = new ServerRpcReceiveParams
                            {
                                UpdateStage = (NetworkUpdateStage)networkUpdateStage,
                                SenderClientId = queueItem.networkId
                            }
                        };
                        break;
                    case RpcQueueContainer.QueueItemType.ClientRpc:
                        rpcParams.Client = new ClientRpcParams
                        {
                            Receive = new ClientRpcReceiveParams
                            {
                                UpdateStage = (NetworkUpdateStage)networkUpdateStage
                            }
                        };
                        break;
                }

                __ntable[networkMethodId](networkBehaviour, new BitSerializer(queueItem.streamReader), rpcParams);
            }
#pragma warning restore 618

#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_InvokeRPC.End();
#endif
        }

        private void BufferCallback(ulong networkId, PreBufferPreset preset)
        {
            if (!preset.AllowBuffer)
            {
                // This is to prevent recursive buffering
                if (NetworkLog.CurrentLogLevel <= LogLevel.Error) NetworkLog.LogError("A message of type " + MLAPIConstants.MESSAGE_NAMES[preset.MessageType] + " was recursivley buffered. It has been dropped.");
                return;
            }

            if (!NetworkConfig.EnableMessageBuffering)
            {
                throw new InvalidOperationException("Cannot buffer with buffering disabled.");
            }

            if (IsServer)
            {
                throw new InvalidOperationException("Cannot buffer on server.");
            }

            BufferManager.BufferMessageForNetworkId(networkId, preset.ClientId, preset.Channel, preset.ReceiveTime, preset.Data);
        }

        /// <summary>
        /// Disconnects the remote client.
        /// </summary>
        /// <param name="clientId">The ClientId to disconnect</param>
        public void DisconnectClient(ulong clientId)
        {
            if (!IsServer)
            {
                throw new NotServerException("Only server can disconnect remote clients. Use StopClient instead.");
            }

            if (ConnectedClients.ContainsKey(clientId))
                ConnectedClients.Remove(clientId);

            if (PendingClients.ContainsKey(clientId))
                PendingClients.Remove(clientId);

            for (int i = ConnectedClientsList.Count - 1; i > -1; i--)
            {
                if (ConnectedClientsList[i].ClientId == clientId) {
                    ConnectedClientsList.RemoveAt(i);
                    PerformanceDataManager.Increment(ProfilerConstants.NumberOfConnections, -1);
                    ProfilerStatManager.connections.Record(-1);
                }
            }

            NetworkConfig.NetworkTransport.DisconnectRemoteClient(clientId);
        }

        internal void OnClientDisconnectFromServer(ulong clientId)
        {
            if (PendingClients.ContainsKey(clientId))
                PendingClients.Remove(clientId);

            if (ConnectedClients.ContainsKey(clientId))
            {
                if (IsServer)
                {
                    if (ConnectedClients[clientId].PlayerObject != null)
                    {
                        if (SpawnManager.customDestroyHandlers.ContainsKey(ConnectedClients[clientId].PlayerObject.PrefabHash))
                        {
                            SpawnManager.customDestroyHandlers[ConnectedClients[clientId].PlayerObject.PrefabHash](ConnectedClients[clientId].PlayerObject);
                            SpawnManager.OnDestroyObject(ConnectedClients[clientId].PlayerObject.NetworkId, false);
                        }
                        else
                        {
                            Destroy(ConnectedClients[clientId].PlayerObject.gameObject);
                        }
                    }

                    for (int i = 0; i < ConnectedClients[clientId].OwnedObjects.Count; i++)
                    {
                        if (ConnectedClients[clientId].OwnedObjects[i] != null)
                        {
                            if (!ConnectedClients[clientId].OwnedObjects[i].DontDestroyWithOwner)
                            {
                                if (SpawnManager.customDestroyHandlers.ContainsKey(ConnectedClients[clientId].OwnedObjects[i].PrefabHash))
                                {
                                    SpawnManager.customDestroyHandlers[ConnectedClients[clientId].OwnedObjects[i].PrefabHash](ConnectedClients[clientId].OwnedObjects[i]);
                                    SpawnManager.OnDestroyObject(ConnectedClients[clientId].OwnedObjects[i].NetworkId, false);
                                }
                                else
                                {
                                    Destroy(ConnectedClients[clientId].OwnedObjects[i].gameObject);
                                }
                            }
                            else
                            {
                                ConnectedClients[clientId].OwnedObjects[i].RemoveOwnership();
                            }
                        }
                    }

                    // TODO: Could(should?) be replaced with more memory per client, by storing the visiblity

                    foreach (var sobj in SpawnManager.SpawnedObjectsList)
                    {
                        sobj.observers.Remove(clientId);
                    }
                }

                for (int i = 0; i < ConnectedClientsList.Count; i++)
                {
                    if (ConnectedClientsList[i].ClientId == clientId)
                    {
                        ConnectedClientsList.RemoveAt(i);
                        PerformanceDataManager.Increment(ProfilerConstants.NumberOfConnections, -1);
                        ProfilerStatManager.connections.Record(-1);
                        break;
                    }
                }

                ConnectedClients.Remove(clientId);
            }
        }

        private void SyncTime()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_SyncTime.Begin();
#endif
            if (NetworkLog.CurrentLogLevel <= LogLevel.Developer) NetworkLog.LogInfo("Syncing Time To Clients");
            using (PooledBitStream stream = PooledBitStream.Get())
            {
                using (PooledBitWriter writer = PooledBitWriter.Get(stream))
                {
                    writer.WriteSinglePacked(Time.realtimeSinceStartup);
                    InternalMessageSender.Send(MLAPIConstants.MLAPI_TIME_SYNC, Channel.SyncChannel, stream);
                }
            }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            s_SyncTime.End();
#endif
        }

        private readonly List<NetworkedObject> _observedObjects = new List<NetworkedObject>();

        internal void HandleApproval(ulong clientId, bool createPlayerObject, ulong? playerPrefabHash, bool approved, Vector3? position, Quaternion? rotation)
        {
            if (approved)
            {
                // Inform new client it got approved
                if (PendingClients.ContainsKey(clientId))
                    PendingClients.Remove(clientId);
                NetworkedClient client = new NetworkedClient()
                {
                    ClientId = clientId,
                };
                ConnectedClients.Add(clientId, client);
                ConnectedClientsList.Add(client);

                PerformanceDataManager.Increment(ProfilerConstants.NumberOfConnections);
                ProfilerStatManager.connections.Record();

                // This packet is unreliable, but if it gets through it should provide a much better sync than the potentially huge approval message.
                SyncTime();


                if (createPlayerObject)
                {
                    NetworkedObject netObject = SpawnManager.CreateLocalNetworkedObject(false, 0, (playerPrefabHash == null ? NetworkConfig.PlayerPrefabHash.Value : playerPrefabHash.Value), null, position, rotation);
                    SpawnManager.SpawnNetworkedObjectLocally(netObject, SpawnManager.GetNetworkObjectId(), false, true, clientId, null, false, 0, false, false);

                    ConnectedClients[clientId].PlayerObject = netObject;
                }

                _observedObjects.Clear();

                foreach (var sobj in SpawnManager.SpawnedObjectsList)
                {
                    if (clientId == ServerClientId || sobj.CheckObjectVisibility == null || sobj.CheckObjectVisibility(clientId))
                    {
                        _observedObjects.Add(sobj);
                        sobj.observers.Add(clientId);
                    }
                }

                using (PooledBitStream stream = PooledBitStream.Get())
                {
                    using (PooledBitWriter writer = PooledBitWriter.Get(stream))
                    {
                        writer.WriteUInt64Packed(clientId);

                        if (NetworkConfig.EnableSceneManagement)
                        {
                            writer.WriteUInt32Packed(NetworkSceneManager.currentSceneIndex);
                            writer.WriteByteArray(NetworkSceneManager.currentSceneSwitchProgressGuid.ToByteArray());
                        }

                        writer.WriteSinglePacked(Time.realtimeSinceStartup);

                        writer.WriteUInt32Packed((uint)_observedObjects.Count);

                        for (int i = 0; i < _observedObjects.Count; i++)
                        {
                            NetworkedObject observedObject = _observedObjects[i];
                            writer.WriteBool(observedObject.IsPlayerObject);
                            writer.WriteUInt64Packed(observedObject.NetworkId);
                            writer.WriteUInt64Packed(observedObject.OwnerClientId);

                            NetworkedObject parent = null;

                            if (!observedObject.AlwaysReplicateAsRoot && observedObject.transform.parent != null)
                            {
                                parent = observedObject.transform.parent.GetComponent<NetworkedObject>();
                            }

                            if (parent == null)
                            {
                                writer.WriteBool(false);
                            }
                            else
                            {
                                writer.WriteBool(true);
                                writer.WriteUInt64Packed(parent.NetworkId);
                            }

                            if (!NetworkConfig.EnableSceneManagement || NetworkConfig.UsePrefabSync)
                            {
                                writer.WriteUInt64Packed(observedObject.PrefabHash);
                            }
                            else
                            {
                                // Is this a scene object that we will soft map
                                writer.WriteBool(observedObject.IsSceneObject == null ? true : observedObject.IsSceneObject.Value);

                                if (observedObject.IsSceneObject == null || observedObject.IsSceneObject.Value == true)
                                {
                                    writer.WriteUInt64Packed(observedObject.NetworkedInstanceId);
                                }
                                else
                                {
                                    writer.WriteUInt64Packed(observedObject.PrefabHash);
                                }
                            }

                            if (observedObject.IncludeTransformWhenSpawning == null || observedObject.IncludeTransformWhenSpawning(clientId))
                            {
                                writer.WriteBool(true);
                                writer.WriteSinglePacked(observedObject.transform.position.x);
                                writer.WriteSinglePacked(observedObject.transform.position.y);
                                writer.WriteSinglePacked(observedObject.transform.position.z);

                                writer.WriteSinglePacked(observedObject.transform.rotation.eulerAngles.x);
                                writer.WriteSinglePacked(observedObject.transform.rotation.eulerAngles.y);
                                writer.WriteSinglePacked(observedObject.transform.rotation.eulerAngles.z);
                            }
                            else
                            {
                                writer.WriteBool(false);
                            }

                            if (NetworkConfig.EnableNetworkedVar)
                            {
                                observedObject.WriteNetworkedVarData(stream, clientId);
                            }
                        }

                        InternalMessageSender.Send(clientId, MLAPIConstants.MLAPI_CONNECTION_APPROVED, Channel.Internal, stream);

                        if (OnClientConnectedCallback != null)
                            OnClientConnectedCallback.Invoke(clientId);
                    }
                }

                if(!createPlayerObject || (playerPrefabHash == null && NetworkConfig.PlayerPrefabHash == null))
                    return;

                //Inform old clients of the new player

                foreach (KeyValuePair<ulong, NetworkedClient> clientPair in ConnectedClients)
                {
                    if (clientPair.Key == clientId || ConnectedClients[clientId].PlayerObject == null || !ConnectedClients[clientId].PlayerObject.observers.Contains(clientPair.Key))
                        continue; //The new client.

                    using (PooledBitStream stream = PooledBitStream.Get())
                    {
                        using (PooledBitWriter writer = PooledBitWriter.Get(stream))
                        {
                            writer.WriteBool(true);
                            writer.WriteUInt64Packed(ConnectedClients[clientId].PlayerObject.NetworkId);
                            writer.WriteUInt64Packed(clientId);

                            //Does not have a parent
                            writer.WriteBool(false);

                            if (!NetworkConfig.EnableSceneManagement || NetworkConfig.UsePrefabSync)
                            {
                                writer.WriteUInt64Packed(playerPrefabHash == null ? NetworkConfig.PlayerPrefabHash.Value : playerPrefabHash.Value);
                            }
                            else
                            {
                                // Not a softmap aka scene object
                                writer.WriteBool(false);
                                writer.WriteUInt64Packed(playerPrefabHash == null ? NetworkConfig.PlayerPrefabHash.Value : playerPrefabHash.Value);
                            }

                            if (ConnectedClients[clientId].PlayerObject.IncludeTransformWhenSpawning == null || ConnectedClients[clientId].PlayerObject.IncludeTransformWhenSpawning(clientId))
                            {
                                writer.WriteBool(true);
                                writer.WriteSinglePacked(ConnectedClients[clientId].PlayerObject.transform.position.x);
                                writer.WriteSinglePacked(ConnectedClients[clientId].PlayerObject.transform.position.y);
                                writer.WriteSinglePacked(ConnectedClients[clientId].PlayerObject.transform.position.z);

                                writer.WriteSinglePacked(ConnectedClients[clientId].PlayerObject.transform.rotation.eulerAngles.x);
                                writer.WriteSinglePacked(ConnectedClients[clientId].PlayerObject.transform.rotation.eulerAngles.y);
                                writer.WriteSinglePacked(ConnectedClients[clientId].PlayerObject.transform.rotation.eulerAngles.z);
                            }
                            else
                            {
                                writer.WriteBool(false);
                            }

                            writer.WriteBool(false); //No payload data

                            if (NetworkConfig.EnableNetworkedVar)
                            {
                                ConnectedClients[clientId].PlayerObject.WriteNetworkedVarData(stream, clientPair.Key);
                            }

                            InternalMessageSender.Send(clientPair.Key, MLAPIConstants.MLAPI_ADD_OBJECT, Channel.Internal, stream);
                        }
                    }
                }
            }
            else
            {
                if (PendingClients.ContainsKey(clientId))
                    PendingClients.Remove(clientId);

                NetworkConfig.NetworkTransport.DisconnectRemoteClient(clientId);
            }
        }
    }
}