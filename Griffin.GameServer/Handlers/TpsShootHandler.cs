using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Third-person shooter mini game (TpsShoot) of the events. There was no handler, so Info came back empty (every stage locked) and
    /// Finish was never recorded. The stages are TpsshootMissionData (1 = stage, 3 = theme, 7 = stage that must be cleared first). Each
    /// stage record (NIADMJDPIDM) carries rank, time, score and the unlocked flag (field 3, as in the other mini games).
    /// ASSUMPTION A-191: a run with a rank above 0 clears the stage; the best rank is the lowest non-zero one, the best score the highest.
    /// </summary>
    public sealed class TpsShootHandler(IAccountStateService accountState, ITableService tableService) : TpsShootHandlerBase
    {
        public sealed class Record
        {
            public uint Rank { get; set; }
            public uint Duration { get; set; }
            public uint Score { get; set; }
            public uint Hp { get; set; }
            public bool Cleared { get; set; }
        }

        private static string Key(uint theme) => $"tpsshoot.{theme}";

        private Dictionary<uint, Record> Load(uint uid, uint theme) => accountState.Get<Dictionary<uint, Record>>(uid, Key(theme)) ?? [];

        private SC_TpsShootInfo Info(uint uid, uint theme)
        {
            Dictionary<uint, Record> records = Load(uid, theme);
            SC_TpsShootInfo reply = new() { ThemeId = theme };
            foreach (Google.Protobuf.IMessage row in tableService.GetTable<TpsshootMissionData>().Where(r => TableFields.U(r, 3) == theme))
            {
                uint stage = TableFields.U(row, 1);
                uint previous = TableFields.U(row, 7);
                records.TryGetValue(stage, out Record? record);
                reply.KNMAKEJFGDK[stage] = new NIADMJDPIDM
                {
                    MPNGHAKGLDM = previous == 0 || (records.TryGetValue(previous, out Record? before) && before.Cleared),
                    Rank = record?.Rank ?? 0,
                    Duration = record?.Duration ?? 0,
                    Score = record?.Score ?? 0,
                    CHBFHIPAOIE = record?.Hp ?? 0,
                };
            }

            reply.JHONJAGOIHM.AddRange(records.Where(p => p.Value.Cleared).Select(p => p.Key).Order());
            return reply;
        }

        public override void HandleTpsShootInfo(CS_TpsShootInfo request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(Info(connection.Account.Uid, request.ThemeId));
        }

        public override void HandleTpsShootStart(CS_TpsShootStart request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_TpsShootStart());
        }

        public override void HandleTpsShootFinish(CS_TpsShootFinish request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            Record record;
            lock (AccountLock.For(uid))
            {
                Dictionary<uint, Record> records = Load(uid, request.ThemeId);
                record = records.GetValueOrDefault(request.StageId) ?? new Record();
                if (request.Rank > 0)
                {
                    record.Cleared = true;
                    record.Rank = record.Rank == 0 ? request.Rank : Math.Min(record.Rank, request.Rank);
                    record.Duration = record.Duration == 0 ? request.Duration : Math.Min(record.Duration, request.Duration);
                }

                record.Score = Math.Max(record.Score, request.Score);
                record.Hp = Math.Max(record.Hp, request.CHBFHIPAOIE);
                records[request.StageId] = record;
                accountState.Set(uid, Key(request.ThemeId), records);
            }

            uint unlocked = record.Cleared
                ? tableService.GetTable<TpsshootMissionData>().Where(r => TableFields.U(r, 7) == request.StageId).Select(r => TableFields.U(r, 1)).FirstOrDefault()
                : 0;
            SC_TpsShootInfo info = Info(uid, request.ThemeId);
            SC_TpsShootFinish reply = new()
            {
                ThemeId = request.ThemeId,
                StageId = request.StageId,
                Rank = record.Rank,
                Duration = record.Duration,
                Score = request.Score,
                CHBFHIPAOIE = request.CHBFHIPAOIE,
                UnlockStage = unlocked,
            };
            reply.JHONJAGOIHM.AddRange(info.JHONJAGOIHM);
            connection.Send(reply);
        }

        public override void HandleTpsShootRank(CS_TpsShootRank request, Connection connection)
        {
            // a local server has no friends nor guild to rank against
            if (connection.Account is not null)
                connection.Send(new SC_TpsShootRank { ThemeId = request.ThemeId, OCGNAONELAA = (SC_TpsShootRank.Types.CNFJFPEBJLA)(int)request.OCGNAONELAA });
        }
    }
}
