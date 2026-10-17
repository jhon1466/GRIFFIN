using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class PlanHandler(ITableService tableService) : PlanHandlerBase
    {
        public override void HandlePlan(CS_Plan req, Connection connection)
        {
            // Use the installed, versioned PlanData and the same game clock as SC_Sync.
            ulong now = checked((ulong)connection.GameClockSeconds());
            List<PlanData> active = tableService.GetTable<PlanData>()
                .Where(row => TableFields.U(row, 2) == req.Type)
                // PlanData column 3 is the open-time *kind* enum. Column 4 is
                // the actual Unix timestamp (MNNEDNOKNCE), followed by close_time.
                .Where(row => TableFields.U64(row, 4) <= now
                    && (TableFields.U64(row, 5) == 0 || now <= TableFields.U64(row, 5)))
                .OrderBy(row => TableFields.U(row, 1))
                .ToList();

            SC_Plan response = new()
            {
                Type = req.Type,
                Id = active.Count == 0 ? 0 : TableFields.U(active[0], 1),
            };

            foreach (PlanData row in active)
            {
                Plan plan = new()
                {
                    Id = TableFields.U(row, 1),
                    Type = TableFields.U(row, 2),
                    OpenTime = checked((long)TableFields.U64(row, 4)),
                    CloseTime = checked((long)TableFields.U64(row, 5)),
                };
                plan.Args.AddRange(TableFields.List(row, 6));
                response.Plans.Add(plan);
            }

            connection.Send(response);
        }
    }
}
