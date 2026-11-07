using Griffin.Command;
using Griffin.Common.Table;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Commands;

[Command("evento", "Abre o cierra cualquier evento que haya salido en el juego", "evento buscar <texto> | evento abrir <id> [dias] | evento cerrar <id> | evento activos", CommandSource.All)]
public sealed class EventoCommand(ITableService tableService, IEventOverrideService overrides) : ICommand
{
    public void Execute(CommandContext ctx)
    {
        List<string> words = ctx.RawArgs.Select(p => string.Equals(p.Value, "true", StringComparison.OrdinalIgnoreCase) ? p.Key : $"{p.Key}={p.Value}").ToList();
        string action = words.FirstOrDefault()?.ToLowerInvariant() ?? "activos";
        List<EventCatalog.Entry> all = EventCatalog.All(tableService, overrides);

        switch (action)
        {
            case "buscar":
            {
                string text = string.Join(' ', words.Skip(1));
                var found = all.Where(e => text.Length == 0 || e.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || e.Id.ToString() == text).TakeLast(15).ToList();
                ctx.Reply(found.Count == 0 ? "No hay eventos con ese texto." : string.Join('\n', found.Select(Line)));
                break;
            }
            case "abrir":
            {
                if (words.Count < 2 || !uint.TryParse(words[1], out uint id) || all.All(e => e.Id != id))
                {
                    ctx.Reply("Uso: evento abrir <id> [dias]. El id sale de: evento buscar <texto>");
                    return;
                }

                int days = words.Count > 2 && int.TryParse(words[2], out int d) ? d : 14;
                EventOverrideService.Window w = overrides.Open(id, days);
                ctx.Reply($"Evento {id} abierto hasta {DateTimeOffset.FromUnixTimeSeconds(w.Close):yyyy-MM-dd}. Vuelve a entrar al juego para verlo.");
                break;
            }
            case "cerrar":
            {
                if (words.Count < 2 || !uint.TryParse(words[1], out uint id))
                {
                    ctx.Reply("Uso: evento cerrar <id>");
                    return;
                }

                ctx.Reply(overrides.Close(id) ? $"Evento {id} vuelve a sus fechas oficiales." : $"El evento {id} no estaba abierto a mano.");
                break;
            }
            default:
            {
                var open = all.Where(e => e.Open is not null).ToList();
                ctx.Reply(open.Count == 0 ? "No hay eventos abiertos a mano." : string.Join('\n', open.Select(Line)));
                break;
            }
        }
    }

    private static string Line(EventCatalog.Entry e) =>
        $"{e.Id} [tipo {e.Type}] {e.Name} ({DateTimeOffset.FromUnixTimeSeconds(e.OfficialOpen):yyyy-MM-dd})" + (e.Open is not null ? " ABIERTO" : "");
}
