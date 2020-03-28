# if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
using Grpc.Core;
#endif
#if UNITY_EDITOR
using UnityEditor;
#endif
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using MLAgents.CommunicatorObjects;
using MLAgents.Sensors;
using MLAgents.Policies;
using MLAgents.SideChannels;
using System.IO;
using Google.Protobuf;

namespace MLAgents
{
    /// Responsible for communication with External using gRPC.
    internal class RpcCommunicator : ICommunicator
    {
        public event QuitCommandHandler QuitCommandReceived;
        public event ResetCommandHandler ResetCommandReceived;

        /// If true, the communication is active.
        bool m_IsOpen;

        List<string> m_BehaviorNames = new List<string>();
        bool m_NeedCommunicateThisStep;
        WriteAdapter m_WriteAdapter = new WriteAdapter();
        Dictionary<string, SensorShapeValidator> m_SensorShapeValidators = new Dictionary<string, SensorShapeValidator>();
        Dictionary<string, List<int>> m_OrderedAgentsRequestingDecisions = new Dictionary<string, List<int>>();

        /// The current UnityRLOutput to be sent when all the brains queried the communicator
        UnityRLOutputProto m_CurrentUnityRlOutput =
            new UnityRLOutputProto();

        Dictionary<string, Dictionary<int, float[]>> m_LastActionsReceived =
            new Dictionary<string, Dictionary<int, float[]>>();

        // Brains that we have sent over the communicator with agents.
        HashSet<string> m_SentBrainKeys = new HashSet<string>();
        Dictionary<string, BrainParameters> m_UnsentBrainKeys = new Dictionary<string, BrainParameters>();


# if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
        /// The Unity to External client.
        UnityToExternalProto.UnityToExternalProtoClient m_Client;
#endif
        /// The communicator parameters sent at construction
        CommunicatorInitParameters m_CommunicatorInitParameters;

        Dictionary<Guid, SideChannel> m_SideChannels = new Dictionary<Guid, SideChannel>();

        /// <summary>
        /// Initializes a new instance of the RPCCommunicator class.
        /// </summary>
        /// <param name="communicatorInitParameters">Communicator parameters.</param>
        public RpcCommunicator(CommunicatorInitParameters communicatorInitParameters)
        {
            m_CommunicatorInitParameters = communicatorInitParameters;
        }

        #region Initialization

        /// <summary>
        /// Sends the initialization parameters through the Communicator.
        /// Is used by the academy to send initialization parameters to the communicator.
        /// </summary>
        /// <returns>The External Initialization Parameters received.</returns>
        /// <param name="initParameters">The Unity Initialization Parameters to be sent.</param>
        public UnityRLInitParameters Initialize(CommunicatorInitParameters initParameters)
        {
            var academyParameters = new UnityRLInitializationOutputProto
            {
                Name = initParameters.name,
                PackageVersion = initParameters.unityPackageVersion,
                CommunicationVersion = initParameters.unityCommunicationVersion
            };

            UnityInputProto input;
            UnityInputProto initializationInput;
            try
            {
                initializationInput = Initialize(
                    new UnityOutputProto
                    {
                        RlInitializationOutput = academyParameters
                    },
                    out input);

                // Initialization succeeded part-way. The most likely cause is a mismatch between the communicator
                // API strings, so log an explicit warning if that's the case.
                if (initializationInput != null && input == null)
                {
                    var pythonCommunicationVersion = initializationInput.RlInitializationInput.CommunicationVersion;
                    var pythonPackageVersion = initializationInput.RlInitializationInput.PackageVersion;
                    if (pythonCommunicationVersion != initParameters.unityCommunicationVersion)
                    {
                        Debug.LogWarningFormat(
                            "Communication protocol between python ({0}) and Unity ({1}) don't match. " +
                            "Python library version: {2}.",
                            pythonCommunicationVersion, initParameters.unityCommunicationVersion,
                            pythonPackageVersion
                        );
                    }
                    else
                    {
                        Debug.LogWarningFormat(
                            "Unknown communication error between Python. Python communication protocol: {0}, " +
                            "Python library version: {1}.",
                            pythonCommunicationVersion,
                            pythonPackageVersion
                        );
                    }

                    throw new UnityAgentsException("ICommunicator.Initialize() failed.");
                }
            }
            catch
            {
                var exceptionMessage = "The Communicator was unable to connect. Please make sure the External " +
                    "process is ready to accept communication with Unity.";

                // Check for common error condition and add details to the exception message.
                var httpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY");
                var httpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
                if (httpProxy != null || httpsProxy != null)
                {
                    exceptionMessage += " Try removing HTTP_PROXY and HTTPS_PROXY from the" +
                        "environment variables and try again.";
                }
                throw new UnityAgentsException(exceptionMessage);
            }

            UpdateEnvironmentWithInput(input.RlInput);
            return initializationInput.RlInitializationInput.ToUnityRLInitParameters();
        }

        /// <summary>
        /// Adds the brain to the list of brains which will be sending information to External.
        /// </summary>
        /// <param name="brainKey">Brain key.</param>
        /// <param name="brainParameters">Brain parameters needed to send to the trainer.</param>
        public void SubscribeBrain(string brainKey, BrainParameters brainParameters)
        {
            if (m_BehaviorNames.Contains(brainKey))
            {
                return;
            }
            m_BehaviorNames.Add(brainKey);
            m_CurrentUnityRlOutput.AgentInfos.Add(
                brainKey,
                new UnityRLOutputProto.Types.ListAgentInfoProto()
            );

            CacheBrainParameters(brainKey, brainParameters);
        }

        void UpdateEnvironmentWithInput(UnityRLInputProto rlInput)
        {
            ProcessSideChannelData(m_SideChannels, rlInput.SideChannel.ToArray());
            SendCommandEvent(rlInput.Command);
        }

        UnityInputProto Initialize(UnityOutputProto unityOutput,
            out UnityInputProto unityInput)
        {
# if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
            m_IsOpen = true;
            var channel = new Channel(
                "localhost:" + m_CommunicatorInitParameters.port,
                ChannelCredentials.Insecure);

            m_Client = new UnityToExternalProto.UnityToExternalProtoClient(channel);
            var result = m_Client.Exchange(WrapMessage(unityOutput, 200));
            unityInput = m_Client.Exchange(WrapMessage(null, 200)).UnityInput;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += HandleOnPlayModeChanged;
#endif
            return result.UnityInput;
#else
            throw new UnityAgentsException(
                "You cannot perform training on this platform.");
#endif
        }

        #endregion

        #region Destruction

        /// <summary>
        /// Close the communicator gracefully on both sides of the communication.
        /// </summary>
        public void Dispose()
        {
# if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
            if (!m_IsOpen)
            {
                return;
            }

            try
            {
                m_Client.Exchange(WrapMessage(null, 400));
                m_IsOpen = false;
            }
            catch
            {
                // ignored
            }
#else
            throw new UnityAgentsException(
                "You cannot perform training on this platform.");
#endif
        }

        #endregion

        #region Sending Events

        void SendCommandEvent(CommandProto command)
        {
            switch (command)
            {
                case CommandProto.Quit:
                {
                    QuitCommandReceived?.Invoke();
                    return;
                }
                case CommandProto.Reset:
                {
                    foreach (var brainName in m_OrderedAgentsRequestingDecisions.Keys)
                    {
                        m_OrderedAgentsRequestingDecisions[brainName].Clear();
                    }
                    ResetCommandReceived?.Invoke();
                    return;
                }
                default:
                {
                    return;
                }
            }
        }

        #endregion

        #region Sending and retreiving data

        public void DecideBatch()
        {
            if (!m_NeedCommunicateThisStep)
            {
                return;
            }
            m_NeedCommunicateThisStep = false;

            SendBatchedMessageHelper();
        }

        /// <summary>
        /// Sends the observations of one Agent.
        /// </summary>
        /// <param name="behaviorName">Batch Key.</param>
        /// <param name="info">Agent info.</param>
        /// <param name="sensors">Sensors that will produce the observations</param>
        public void PutObservations(string behaviorName, AgentInfo info, List<ISensor> sensors)
        {
# if DEBUG
            if (!m_SensorShapeValidators.ContainsKey(behaviorName))
            {
                m_SensorShapeValidators[behaviorName] = new SensorShapeValidator();
            }
            m_SensorShapeValidators[behaviorName].ValidateSensors(sensors);
#endif

            using (TimerStack.Instance.Scoped("AgentInfo.ToProto"))
            {
                var agentInfoProto = info.ToAgentInfoProto();

                using (TimerStack.Instance.Scoped("GenerateSensorData"))
                {
                    foreach (var sensor in sensors)
                    {
                        var obsProto = sensor.GetObservationProto(m_WriteAdapter);
                        agentInfoProto.Observations.Add(obsProto);
                    }
                }
                m_CurrentUnityRlOutput.AgentInfos[behaviorName].Value.Add(agentInfoProto);
            }

            m_NeedCommunicateThisStep = true;
            if (!m_OrderedAgentsRequestingDecisions.ContainsKey(behaviorName))
            {
                m_OrderedAgentsRequestingDecisions[behaviorName] = new List<int>();
            }
            m_OrderedAgentsRequestingDecisions[behaviorName].Add(info.episodeId);
            if (!m_LastActionsReceived.ContainsKey(behaviorName))
            {
                m_LastActionsReceived[behaviorName] = new Dictionary<int, float[]>();
            }
            m_LastActionsReceived[behaviorName][info.episodeId] = null;
            if (info.done)
            {
                m_LastActionsReceived[behaviorName].Remove(info.episodeId);
            }
        }

        /// <summary>
        /// Helper method that sends the current UnityRLOutput, receives the next UnityInput and
        /// Applies the appropriate AgentAction to the agents.
        /// </summary>
        void SendBatchedMessageHelper()
        {
            var message = new UnityOutputProto
            {
                RlOutput = m_CurrentUnityRlOutput,
            };
            var tempUnityRlInitializationOutput = GetTempUnityRlInitializationOutput();
            if (tempUnityRlInitializationOutput != null)
            {
                message.RlInitializationOutput = tempUnityRlInitializationOutput;
            }

            byte[] messageAggregated = GetSideChannelMessage(m_SideChannels);
            message.RlOutput.SideChannel = ByteString.CopyFrom(messageAggregated);

            var input = Exchange(message);
            UpdateSentBrainParameters(tempUnityRlInitializationOutput);

            foreach (var k in m_CurrentUnityRlOutput.AgentInfos.Keys)
            {
                m_CurrentUnityRlOutput.AgentInfos[k].Value.Clear();
            }

            var rlInput = input?.RlInput;

            if (rlInput?.AgentActions == null)
            {
                return;
            }

            UpdateEnvironmentWithInput(rlInput);

            foreach (var brainName in rlInput.AgentActions.Keys)
            {
                if (!m_OrderedAgentsRequestingDecisions[brainName].Any())
                {
                    continue;
                }

                if (!rlInput.AgentActions[brainName].Value.Any())
                {
                    continue;
                }

                var agentActions = rlInput.AgentActions[brainName].ToAgentActionList();
                var numAgents = m_OrderedAgentsRequestingDecisions[brainName].Count;
                for (var i = 0; i < numAgents; i++)
                {
                    var agentAction = agentActions[i];
                    var agentId = m_OrderedAgentsRequestingDecisions[brainName][i];
                    if (m_LastActionsReceived[brainName].ContainsKey(agentId))
                    {
                        m_LastActionsReceived[brainName][agentId] = agentAction.vectorActions;
                    }
                }
            }
            foreach (var brainName in m_OrderedAgentsRequestingDecisions.Keys)
            {
                m_OrderedAgentsRequestingDecisions[brainName].Clear();
            }
        }

        public float[] GetActions(string behaviorName, int agentId)
        {
            if (m_LastActionsReceived.ContainsKey(behaviorName))
            {
                if (m_LastActionsReceived[behaviorName].ContainsKey(agentId))
                {
                    return m_LastActionsReceived[behaviorName][agentId];
                }
            }
            return null;
        }

        /// <summary>
        /// Send a UnityOutput and receives a UnityInput.
        /// </summary>
        /// <returns>The next UnityInput.</returns>
        /// <param name="unityOutput">The UnityOutput to be sent.</param>
        UnityInputProto Exchange(UnityOutputProto unityOutput)
        {
# if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX || UNITY_STANDALONE_LINUX
            if (!m_IsOpen)
            {
                return null;
            }
            try
            {
                var message = m_Client.Exchange(WrapMessage(unityOutput, 200));
                if (message.Header.Status == 200)
                {
                    return message.UnityInput;
                }

                m_IsOpen = false;
                // Not sure if the quit command is actually sent when a
                // non 200 message is received.  Notify that we are indeed
                // quitting.
                QuitCommandReceived?.Invoke();
                return message.UnityInput;
            }
            catch
            {
                m_IsOpen = false;
                QuitCommandReceived?.Invoke();
                return null;
            }
#else
            throw new UnityAgentsException(
                "You cannot perform training on this platform.");
#endif
        }

        /// <summary>
        /// Wraps the UnityOutput into a message with the appropriate status.
        /// </summary>
        /// <returns>The UnityMessage corresponding.</returns>
        /// <param name="content">The UnityOutput to be wrapped.</param>
        /// <param name="status">The status of the message.</param>
        static UnityMessageProto WrapMessage(UnityOutputProto content, int status)
        {
            return new UnityMessageProto
            {
                Header = new HeaderProto { Status = status },
                UnityOutput = content
            };
        }

        void CacheBrainParameters(string behaviorName, BrainParameters brainParameters)
        {
            if (m_SentBrainKeys.Contains(behaviorName))
            {
                return;
            }

            // TODO We should check that if m_unsentBrainKeys has brainKey, it equals brainParameters
            m_UnsentBrainKeys[behaviorName] = brainParameters;
        }

        UnityRLInitializationOutputProto GetTempUnityRlInitializationOutput()
        {
            UnityRLInitializationOutputProto output = null;
            foreach (var behaviorName in m_UnsentBrainKeys.Keys)
            {
                if (m_CurrentUnityRlOutput.AgentInfos.ContainsKey(behaviorName))
                {
                    if (output == null)
                    {
                        output = new UnityRLInitializationOutputProto();
                    }

                    var brainParameters = m_UnsentBrainKeys[behaviorName];
                    output.BrainParameters.Add(brainParameters.ToProto(behaviorName, true));
                }
            }

            return output;
        }

        void UpdateSentBrainParameters(UnityRLInitializationOutputProto output)
        {
            if (output == null)
            {
                return;
            }

            foreach (var brainProto in output.BrainParameters)
            {
                m_SentBrainKeys.Add(brainProto.BrainName);
                m_UnsentBrainKeys.Remove(brainProto.BrainName);
            }
        }

        #endregion


        #region Handling side channels

        /// <summary>
        /// Registers a side channel to the communicator. The side channel will exchange
        /// messages with its Python equivalent.
        /// </summary>
        /// <param name="sideChannel"> The side channel to be registered.</param>
        public void RegisterSideChannel(SideChannel sideChannel)
        {
            var channelId = sideChannel.ChannelId;
            if (m_SideChannels.ContainsKey(channelId))
            {
                throw new UnityAgentsException(string.Format(
                    "A side channel with type index {0} is already registered. You cannot register multiple " +
                    "side channels of the same id.", channelId));
            }

            // Process any messages that we've already received for this channel ID.
            var numMessages = m_CachedMessages.Count;
            for (int i = 0; i < numMessages; i++)
            {
                var cachedMessage = m_CachedMessages.Dequeue();
                if (channelId == cachedMessage.ChannelId)
                {
                    using (var incomingMsg = new IncomingMessage(cachedMessage.Message))
                    {
                        sideChannel.OnMessageReceived(incomingMsg);
                    }
                }
                else
                {
                    m_CachedMessages.Enqueue(cachedMessage);
                }
            }
            m_SideChannels.Add(channelId, sideChannel);
        }

        /// <summary>
        /// Unregisters a side channel from the communicator.
        /// </summary>
        /// <param name="sideChannel"> The side channel to be unregistered.</param>
        public void UnregisterSideChannel(SideChannel sideChannel)
        {
            if (m_SideChannels.ContainsKey(sideChannel.ChannelId))
            {
                m_SideChannels.Remove(sideChannel.ChannelId);
            }
        }

        /// <summary>
        /// Grabs the messages that the registered side channels will send to Python at the current step
        /// into a singe byte array.
        /// </summary>
        /// <param name="sideChannels"> A dictionary of channel type to channel.</param>
        /// <returns></returns>
        public static byte[] GetSideChannelMessage(Dictionary<Guid, SideChannel> sideChannels)
        {
            using (var memStream = new MemoryStream())
            {
                using (var binaryWriter = new BinaryWriter(memStream))
                {
                    foreach (var sideChannel in sideChannels.Values)
                    {
                        var messageList = sideChannel.MessageQueue;
                        foreach (var message in messageList)
                        {
                            binaryWriter.Write(sideChannel.ChannelId.ToByteArray());
                            binaryWriter.Write(message.Count());
                            binaryWriter.Write(message);
                        }
                        sideChannel.MessageQueue.Clear();
                    }
                    return memStream.ToArray();
                }
            }
        }

        private struct CachedSideChannelMessage
        {
            public Guid ChannelId;
            public byte[] Message;
        }

        private static Queue<CachedSideChannelMessage> m_CachedMessages = new Queue<CachedSideChannelMessage>();

        /// <summary>
        /// Separates the data received from Python into individual messages for each registered side channel.
        /// </summary>
        /// <param name="sideChannels">A dictionary of channel type to channel.</param>
        /// <param name="dataReceived">The byte array of data received from Python.</param>
        public static void ProcessSideChannelData(Dictionary<Guid, SideChannel> sideChannels, byte[] dataReceived)
        {
            while (m_CachedMessages.Count != 0)
            {
                var cachedMessage = m_CachedMessages.Dequeue();
                if (sideChannels.ContainsKey(cachedMessage.ChannelId))
                {
                    using (var incomingMsg = new IncomingMessage(cachedMessage.Message))
                    {
                        sideChannels[cachedMessage.ChannelId].OnMessageReceived(incomingMsg);
                    }
                }
                else
                {
                    Debug.Log(string.Format(
                        "Unknown side channel data received. Channel Id is "
                        + ": {0}", cachedMessage.ChannelId));
                }
            }

            if (dataReceived.Length == 0)
            {
                return;
            }
            using (var memStream = new MemoryStream(dataReceived))
            {
                using (var binaryReader = new BinaryReader(memStream))
                {
                    while (memStream.Position < memStream.Length)
                    {
                        Guid channelId = Guid.Empty;
                        byte[] message = null;
                        try
                        {
                            channelId = new Guid(binaryReader.ReadBytes(16));
                            var messageLength = binaryReader.ReadInt32();
                            message = binaryReader.ReadBytes(messageLength);
                        }
                        catch (Exception ex)
                        {
                            throw new UnityAgentsException(
                                "There was a problem reading a message in a SideChannel. Please make sure the " +
                                "version of MLAgents in Unity is compatible with the Python version. Original error : "
                                + ex.Message);
                        }
                        if (sideChannels.ContainsKey(channelId))
                        {
                            using (var incomingMsg = new IncomingMessage(message))
                            {
                                sideChannels[channelId].OnMessageReceived(incomingMsg);
                            }
                        }
                        else
                        {
                            // Don't recognize this ID, but cache it in case the SideChannel that can handle
                            // it is registered before the next call to ProcessSideChannelData.
                            m_CachedMessages.Enqueue(new CachedSideChannelMessage
                            {
                                ChannelId = channelId,
                                Message = message
                            });
                        }
                    }
                }
            }
        }

        #endregion

#if UNITY_EDITOR
        /// <summary>
        /// When the editor exits, the communicator must be closed
        /// </summary>
        /// <param name="state">State.</param>
        void HandleOnPlayModeChanged(PlayModeStateChange state)
        {
            // This method is run whenever the playmode state is changed.
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                Dispose();
            }
        }

#endif
    }
}
