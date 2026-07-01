using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

namespace RblxManager
{
    public class DiscordRpcClient : IDisposable
    {
        private NamedPipeClientStream? _pipe;
        private readonly string _clientId;
        private bool _isClosed = false;

        public string ClientId => _clientId;

        public DiscordRpcClient(string clientId)
        {
            _clientId = clientId;
        }

        public async Task<bool> ConnectAsync()
        {
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    string pipeName = $"discord-ipc-{i}";
                    _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                    await _pipe.ConnectAsync(500);
                    
                    // Send Handshake (Opcode 0)
                    string handshakeJson = $"{{\"v\":1,\"client_id\":\"{_clientId}\"}}";
                    WriteMessage(0, handshakeJson);
                    
                    // Read response handshake (discard payload)
                    byte[] header = new byte[8];
                    await _pipe.ReadExactlyAsync(header, 0, 8);
                    int len = BitConverter.ToInt32(header, 4);
                    byte[] buffer = new byte[len];
                    await _pipe.ReadExactlyAsync(buffer, 0, len);

                    return true;
                }
                catch
                {
                    _pipe?.Dispose();
                    _pipe = null;
                }
            }
            return false;
        }

        private void WriteMessage(int op, string json)
        {
            if (_pipe == null || !_pipe.IsConnected) return;

            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            byte[] header = new byte[8];
            Array.Copy(BitConverter.GetBytes(op), 0, header, 0, 4);
            Array.Copy(BitConverter.GetBytes(jsonBytes.Length), 0, header, 4, 4);

            _pipe.Write(header, 0, header.Length);
            _pipe.Write(jsonBytes, 0, jsonBytes.Length);
            _pipe.Flush();
        }

        public void SetActivity(string details, string state, long startTimestamp, string largeImageKey, string largeImageText)
        {
            try
            {
                if (_pipe == null || !_pipe.IsConnected) return;

                string detailsEscaped = EscapeJsonString(details);
                string stateEscaped = EscapeJsonString(state);

                string payload = "{" +
                    "\"cmd\":\"SET_ACTIVITY\"," +
                    "\"args\":{" +
                        "\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id + "," +
                        "\"activity\":{" +
                            "\"details\":\"" + detailsEscaped + "\"," +
                            "\"state\":\"" + stateEscaped + "\"," +
                            "\"timestamps\":{" +
                                "\"start\":" + startTimestamp +
                            "}," +
                            "\"assets\":{" +
                                "\"large_image\":\"" + largeImageKey + "\"," +
                                "\"large_text\":\"" + largeImageText + "\"" +
                            "}" +
                        "}" +
                    "}," +
                    "\"nonce\":\"" + Guid.NewGuid().ToString() + "\"" +
                "}";

                WriteMessage(1, payload);
            }
            catch { }
        }

        public void ClearActivity()
        {
            try
            {
                if (_pipe == null || !_pipe.IsConnected) return;

                string payload = "{" +
                    "\"cmd\":\"SET_ACTIVITY\"," +
                    "\"args\":{" +
                        "\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id + "," +
                        "\"activity\":null" +
                    "}," +
                    "\"nonce\":\"" + Guid.NewGuid().ToString() + "\"" +
                "}";

                WriteMessage(1, payload);
            }
            catch { }
        }

        private static string EscapeJsonString(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        public void Dispose()
        {
            if (_isClosed) return;
            _isClosed = true;
            try
            {
                ClearActivity();
                _pipe?.Dispose();
            }
            catch { }
        }
    }
}
