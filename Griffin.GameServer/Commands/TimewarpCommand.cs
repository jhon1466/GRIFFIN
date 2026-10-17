using Griffin.Command;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using System.Globalization;

namespace Griffin.GameServer.Commands;

[Command("timewarp", "Cambia el reloj del juego a una hora de servidor personalizada", "timewarp <serverTime> [uid] (or timewarp serverTime=<value> [uid=<value>])", CommandSource.All)]
public sealed class TimewarpCommand(ConnectionManager connectionManager) : ICommand
{
    [Argument("serverTime", "Marca de tiempo Unix (milisegundos) que se usará como hora del juego", flags: ArgumentFlags.Optional)]
    public long ServerTime { get; set; }

    [Argument("uid", "UID del jugador (obligatorio desde la consola)", flags: ArgumentFlags.Optional)]
    public uint Uid { get; set; }

    public void Execute(CommandContext ctx)
    {
        if (!TryResolveArguments(ctx.RawArgs, out long requestedServerTime, out uint requestedUid, out string? argumentError))
        {
            ctx.Reply(argumentError!);
            return;
        }

        Connection? targetConnection = null;
        uint targetUid;

        if (ctx.Connection is not null)
        {
            if (ctx.Connection.Account is null)
            {
                ctx.Reply("Esta conexión aún no tiene una cuenta autenticada.");
                return;
            }

            targetConnection = ctx.Connection;
            targetUid = ctx.Connection.Account.Uid;
        }
        else
        {
            if (requestedUid == 0)
            {
                ctx.Reply("Desde la consola hace falta el uid del jugador. Uso: timewarp <serverTime> <uid>");
                return;
            }

            targetUid = requestedUid;
            foreach (Connection connection in connectionManager.Connections)
            {
                if (connection.Account?.Uid == targetUid)
                {
                    targetConnection = connection;
                    break;
                }
            }

            if (targetConnection is null)
            {
                ctx.Reply($"No hay ninguna conexión activa para el uid {targetUid}.");
                return;
            }
        }

        targetConnection.ServerTimeOverride = requestedServerTime;
        targetConnection.Send(new SC_Sync
        {
            Timestamp = targetConnection.GameClockSeconds(),
            ActiveTime = 0
        });

        ctx.Reply($"Hora del servidor del juego fijada en {requestedServerTime} para el uid {targetUid}.");
    }

    private bool TryResolveArguments(
        IReadOnlyDictionary<string, string> rawArgs,
        out long serverTime,
        out uint uid,
        out string? error)
    {
        serverTime = ServerTime;
        uid = Uid;
        error = null;
        bool hasServerTime = rawArgs.ContainsKey("serverTime");

        if (rawArgs.TryGetValue("serverTime", out string? namedServerTime))
        {
            if (!long.TryParse(namedServerTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out serverTime))
            {
                error = $"Valor de serverTime no válido: '{namedServerTime}'.";
                return false;
            }
        }

        if (rawArgs.TryGetValue("uid", out string? namedUid))
        {
            if (!uint.TryParse(namedUid, NumberStyles.Integer, CultureInfo.InvariantCulture, out uid))
            {
                error = $"Valor de uid no válido: '{namedUid}'.";
                return false;
            }
        }

        List<string> positionalNumbers = [];
        foreach (KeyValuePair<string, string> entry in rawArgs)
        {
            if (entry.Key.Equals("serverTime", StringComparison.OrdinalIgnoreCase)
                || entry.Key.Equals("uid", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(entry.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (long.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                positionalNumbers.Add(entry.Key);
            }
        }

        if (serverTime == 0 && positionalNumbers.Count > 0)
        {
            if (!long.TryParse(positionalNumbers[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out serverTime))
            {
                error = $"Valor de serverTime no válido: '{positionalNumbers[0]}'.";
                return false;
            }

            hasServerTime = true;
        }

        if (uid == 0 && positionalNumbers.Count > 1)
        {
            if (!uint.TryParse(positionalNumbers[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out uid))
            {
                error = $"Valor de uid no válido: '{positionalNumbers[1]}'.";
                return false;
            }
        }

        if (!hasServerTime)
        {
            error = "Falta el argumento obligatorio serverTime. Uso: timewarp <serverTime> [uid]";
            return false;
        }

        return true;
    }
}
