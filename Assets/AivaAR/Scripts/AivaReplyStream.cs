using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Aiva.AivaAR
{
    // Mirrors the same text response topic that the Telepathy WebUI subscribes to.
    public sealed class AivaReplyStream : MonoBehaviour
    {
        private const string Topic = "/telepathic/response";
        private const string SubscriptionId = "aiva-ar-replies";
        // The shared reply stream can include Canvas PNG events as well as text.
        private const int MaxMessageBytes = 12 * 1024 * 1024;
        private CancellationTokenSource _connection;

        public event Action TextArrived;
        public event Action ReplyInterrupted;
        public bool Active => _connection != null;
        public string Status { get; private set; } = "Connect to mirror AIVA’s replies.";

        public async void Connect(string address)
        {
            if (Active) return;
            if (!Uri.TryCreate((address ?? "").Trim(), UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(url.Host)
                || !string.IsNullOrEmpty(url.UserInfo))
            {
                Status = "Enter the HTTPS address you use for the Telepathy WebUI.";
                return;
            }

            var endpoint = new UriBuilder(url)
            {
                Scheme = "wss",
                Path = "/rosbridge",
                Query = "",
                Fragment = ""
            }.Uri;

            using var connection = new CancellationTokenSource();
            _connection = connection;
            Status = "Connecting to Telepathy…";
            try
            {
                using var socket = new ClientWebSocket();
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await socket.ConnectAsync(endpoint, timeout.Token);
                }

                var subscribe = Encoding.UTF8.GetBytes(
                    "{\"op\":\"subscribe\",\"id\":\"" + SubscriptionId + "\",\"topic\":\"" + Topic + "\",\"type\":\"std_msgs/msg/String\"}");
                await socket.SendAsync(new ArraySegment<byte>(subscribe), WebSocketMessageType.Text, true, connection.Token);
                Status = "Listening for AIVA’s WebUI replies.";
                await Listen(socket, connection.Token);
                Status = connection.IsCancellationRequested ? "Disconnected." : "Telepathy closed the connection.";
            }
            catch (OperationCanceledException)
            {
                Status = connection.IsCancellationRequested ? "Disconnected." : "Connection timed out.";
            }
            catch (Exception error) when (error is WebSocketException || error is IOException
                || error is PlatformNotSupportedException || error is InvalidOperationException)
            {
                Status = connection.IsCancellationRequested ? "Disconnected." : "Could not listen to Telepathy. Check the address and network.";
            }
            finally
            {
                _connection = null;
                ReplyInterrupted?.Invoke();
            }
        }

        private async Task Listen(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    if (message.Length + part.Count > MaxMessageBytes)
                        throw new IOException("Telepathy reply exceeded the message size limit.");
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);

                if (token.IsCancellationRequested) return;
                if (part.MessageType == WebSocketMessageType.Text)
                {
                    ReadMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                }
            }
        }

        private void ReadMessage(string json)
        {
            try
            {
                var envelope = JsonUtility.FromJson<Envelope>(json);
                if (envelope?.op == "status" && envelope.id == SubscriptionId && envelope.level == "error")
                    throw new IOException("Telepathy rejected the reply subscription.");

                if (envelope?.op != "publish" || envelope.topic != Topic
                    || string.IsNullOrEmpty(envelope.msg?.data)) return;

                var reply = JsonUtility.FromJson<Reply>(envelope.msg.data);
                if (reply?.type == "delta" && !string.IsNullOrWhiteSpace(reply.content))
                    TextArrived?.Invoke();
                else if (reply?.type == "error")
                    ReplyInterrupted?.Invoke();
            }
            catch (ArgumentException)
            {
                // Only structured AIVA reply messages can drive the avatar.
            }
        }

        public void Disconnect()
        {
            _connection?.Cancel();
            ReplyInterrupted?.Invoke();
        }

        private void OnDestroy() => Disconnect();

        [Serializable]
        private sealed class Envelope
        {
            public string op;
            public string topic;
            public string id;
            public string level;
            public Payload msg;
        }

        [Serializable]
        private sealed class Payload
        {
            public string data;
        }

        [Serializable]
        private sealed class Reply
        {
            public string type;
            public string content;
        }
    }
}
