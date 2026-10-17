using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Griffin.Common.Networking;
using Griffin.Common.Packets;
using Griffin.Common.Protocol;

namespace Griffin.Common.Hosting
{
    public sealed class GameServerHostedService(
           PacketDispatcher dispatcher,
           ConnectionManager connections,
           IConfiguration config,
           ILoggerFactory loggerFactory) : IHostedService
    {
        private readonly ILogger<GameServerHostedService> _logger = loggerFactory.CreateLogger<GameServerHostedService>();
        private readonly IPAddress _address = IPAddress.Parse(
            ReadEnvironmentOverride("GRIFFIN_GAME_SERVER_HOST") ?? config.GetValue("GameServer:Host", "127.0.0.1") ?? "127.0.0.1");
        private readonly int _port = int.TryParse(ReadEnvironmentOverride("GRIFFIN_GAME_SERVER_PORT"), out int envPort)
            ? envPort
            : config.GetValue("GameServer:Port", 7001);

        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _stopping;

        private static string? ReadEnvironmentOverride(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _listener = new TcpListener(_address, _port);
            _listener.Start();
            _logger.LogInformation("GameServer listening on {Address}:{Port}", _address, _port);

            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "GameServer-Accept" };
            _acceptThread.Start();
            return Task.CompletedTask;
        }

        private void AcceptLoop()
        {
            var connectionLogger = loggerFactory.CreateLogger<Connection>();

            try
            {
                while (!_stopping)
                {
                    TcpClient client = _listener!.AcceptTcpClient();

                    var connection = new Connection(
                        client, dispatcher, connections, connectionLogger);

                    var thread = new Thread(connection.Run) { IsBackground = true };
                    thread.Start();
                }
            }
            catch (SocketException) when (_stopping)
            {
                // listener stopped during shutdown
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _stopping = true;

            _logger.LogInformation("GameServer stopping; closing {Count} connection(s)", connections.Count);
            foreach (var connection in connections.Connections)
                connection.Close();

            _listener?.Stop();
            return Task.CompletedTask;
        }
    }
}
