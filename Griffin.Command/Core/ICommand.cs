namespace Griffin.Command;

public interface ICommand
{
    void Execute(CommandContext context);
}
