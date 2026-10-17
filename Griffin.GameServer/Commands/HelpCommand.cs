using System.Text;
using Griffin.Command;

namespace Griffin.GameServer.Commands;

[Command("help", "Lista los comandos disponibles o muestra la ayuda de uno", "help [command]")]
public sealed class HelpCommand(CommandRegistry registry) : ICommand
{
    [Argument("command", "Comando del que mostrar la ayuda detallada", flags: ArgumentFlags.Optional)]
    public string? Command { get; set; }

    public void Execute(CommandContext ctx)
    {
        var commands = registry.GetAll()
            .Where(r => r.AllowedSources.HasFlag(ctx.Source))
            .ToList();

        var target = ResolveTargetCommand(ctx, commands);
        if (target is not null)
        {
            if (!registry.TryGet(target, out var reg))
            {
                ctx.Reply($"Comando desconocido: {target}. Escribe 'help' para ver los comandos disponibles.");
                return;
            }

            ctx.Reply(CommandHelpFormatter.FormatDetail(reg));
            return;
        }

        ctx.Reply(CommandHelpFormatter.FormatSummary(commands));
    }

    private string? ResolveTargetCommand(CommandContext ctx, IReadOnlyList<CommandRegistration> commands)
    {
        if (!string.IsNullOrWhiteSpace(Command))
            return Command;

        foreach (var (key, value) in ctx.RawArgs)
        {
            if (value != "true")
                continue;

            if (commands.Any(c => c.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
                return key;
        }

        return null;
    }

    private static class CommandHelpFormatter
    {
        private const int DetailNameWidth = 46;
        private const int SummaryMinNameWidth = 16;
        private const int MaxLineWidth = 100;

        public static string FormatSummary(IEnumerable<CommandRegistration> commands)
        {
            var list = commands.OrderBy(c => c.Name).ToList();
            var sb = new StringBuilder();

            sb.AppendLine("Comandos de Griffin");
            sb.AppendLine();
            sb.AppendLine("Para más información de un comando, escribe help <comando>");
            sb.AppendLine();

            var nameWidth = Math.Max(SummaryMinNameWidth, list.Count > 0 ? list.Max(c => c.Name.Length) : 0);
            foreach (var reg in list)
            {
                sb.Append(reg.Name.PadRight(nameWidth + 2));
                sb.AppendLine(WrapText(reg.Description, nameWidth + 2, MaxLineWidth));
            }

            return sb.ToString().TrimEnd();
        }

        public static string FormatDetail(CommandRegistration reg)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"{reg.Name} - {reg.Description}");
            sb.AppendLine();
            sb.AppendLine($"Uso: {CommandUsageFormatter.BuildSyntax(reg)}");

            if (reg.Arguments.Count > 0)
            {
                sb.AppendLine();
                foreach (var arg in reg.Arguments)
                {
                    var description = arg.Description;
                    if (arg.Required)
                        description = string.IsNullOrEmpty(description) ? "Obligatorio." : $"Obligatorio. {description}";

                    AppendOption(sb, arg.Name, description, DetailNameWidth);
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static void AppendOption(StringBuilder sb, string name, string description, int nameWidth)
        {
            var prefix = $"  {name}".PadRight(nameWidth + 2);
            AppendWrappedBlock(sb, prefix, description, MaxLineWidth);
        }

        private static string WrapText(string text, int firstLineIndent, int maxWidth)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var sb = new StringBuilder();
            AppendWrappedBlock(sb, new string(' ', firstLineIndent), text, maxWidth);
            return sb.ToString().TrimEnd();
        }

        private static void AppendWrappedBlock(StringBuilder sb, string firstLinePrefix, string text, int maxWidth)
        {
            var continuationPrefix = new string(' ', firstLinePrefix.Length);
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var line = firstLinePrefix;
            var isFirstLine = true;

            foreach (var word in words)
            {
                if (line.Length + word.Length + (line.Length > firstLinePrefix.Length ? 1 : 0) > maxWidth)
                {
                    sb.AppendLine(line.TrimEnd());
                    line = continuationPrefix;
                    isFirstLine = false;
                }

                if (line.Length > (isFirstLine ? firstLinePrefix.Length : continuationPrefix.Length))
                    line += ' ';
                line += word;
            }

            if (line.Length > 0)
                sb.AppendLine(line.TrimEnd());
        }
    }
}
