using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Themes of the EX combat modes (<c>ExThemeData</c>: 1 = theme, 9 = reward). The client keeps only "theme -> reward already picked" from this answer
    /// (it uses it for the red dot); the pick itself is <see cref="RewardHandler"/>. ASSUMPTION A-21: every theme is announced, picked or not, and a
    /// reward can be picked once per theme without further conditions.
    /// </summary>
    public sealed class ExHandler(ITableService tableService, IExThemeService themes) : ExHandlerBase
    {
        public override void HandleExGetThemesInfo(CS_ExGetThemesInfo request, Connection connection)
        {
            SC_ExGetThemesInfo reply = new() { Info = new JIOOMAOFEMF() };
            if (connection.Account is not null)
            {
                IReadOnlySet<uint> picked = themes.Picked(connection.Account.Uid);
                foreach (ExThemeData row in tableService.GetTable<ExThemeData>())
                    reply.Info.LOJGCKOAEHP[row.Id] = new JIOOMAOFEMF.Types.LMPFOBEEDPO { ThemeId = row.Id, DDHPEIJCEME = picked.Contains(row.Id) };
            }

            connection.Send(reply);
        }
    }
}
