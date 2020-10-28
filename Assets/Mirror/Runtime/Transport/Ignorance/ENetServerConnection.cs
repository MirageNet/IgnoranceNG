#region Statements

using Cysharp.Threading.Tasks;
using ENet;
using UnityEngine;

#endregion

namespace Mirror.ENet
{
    public class ENetServerConnection : Common
    {
        public ENetServerConnection(Peer client, Configuration config) : base(client, config)
        {
        }

        protected override async UniTaskVoid ProcessMessages()
        {
            // Only process messages if the client is valid.
            while (!CancelToken.IsCancellationRequested)
            {
                while (OutgoingQueuedData.TryDequeue(out IgnoranceOutgoingMessage message))
                {
                    int returnCode = Client.Send(message.ChannelId, ref message.Payload);

                    if (returnCode == 0)
                    {
                        if (Config.DebugEnabled)
                            Debug.Log($"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {message.ChannelId} OK");

                        await UniTask.Delay(1);

                        continue;
                    }

                    if (Config.DebugEnabled)
                        Debug.Log(
                            $"[DEBUGGING MODE] Ignorance: Outgoing packet on channel {message.ChannelId} FAIL, code {returnCode}");

                    await UniTask.Delay(1);
                }

                await UniTask.Delay(1);
            }
        }
    }
}
