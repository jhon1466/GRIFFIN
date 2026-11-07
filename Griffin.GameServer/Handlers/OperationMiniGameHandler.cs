using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Mini games of the operation events: candy catch (ActivityListData type 30) and card flip (type 29). The event row names the game in
    /// column 11 (CandycatchMainData / CardflipMainData id, whose column 2 is the stage group). Stages (…StageData: 1 = id, 2 = stage that
    /// must be cleared first, group in 4 for candy / 3 for cards, 6 or 7 = its stars) unlock in a chain; a star (…StarData: 3 = reward,
    /// 4 = score needed, none = clearing is enough) pays its reward once. There was no handler, so the stages never opened.
    /// ASSUMPTION A-195: the stage record fields are 1 = cleared, 2 = unlocked, 3 = best score, last = stars earned.
    /// </summary>
    public sealed class OperationMiniGameHandler(IAccountStateService accountState, ITableService tableService, IRewardService rewardService) : OperationMiniGameHandlerBase
    {
        public sealed class Record
        {
            public uint Best { get; set; }
            public bool Cleared { get; set; }
            public List<uint> Stars { get; set; } = [];
            public uint LastResult { get; set; }
        }

        private sealed record Stage(uint Id, uint Previous, IReadOnlyList<uint> Stars);

        private static string Key(uint activityId) => $"operationminigame.{activityId}";

        private (bool Candy, List<Stage> Stages) Game(uint activityId)
        {
            ActivityListData? row = tableService.GetTable<ActivityListData>().FirstOrDefault(r => r.Id == activityId);
            uint main = uint.TryParse(row is null ? "" : TableFields.S(row, 11).Trim('\''), out uint m) ? m : 0;
            bool candy = row is not null && TableFields.U(row, 2) == 30;
            if (candy)
            {
                uint group = tableService.GetTable<CandycatchMainData>().Where(r => TableFields.U(r, 1) == main).Select(r => TableFields.U(r, 2)).FirstOrDefault();
                return (true, tableService.GetTable<CandycatchStageData>().Where(r => TableFields.U(r, 4) == group)
                    .Select(r => new Stage(TableFields.U(r, 1), TableFields.U(r, 2), TableFields.List(r, 6))).ToList());
            }

            uint cardGroup = tableService.GetTable<CardflipMainData>().Where(r => TableFields.U(r, 1) == main).Select(r => TableFields.U(r, 2)).FirstOrDefault();
            return (false, tableService.GetTable<CardflipStageData>().Where(r => TableFields.U(r, 3) == cardGroup)
                .Select(r => new Stage(TableFields.U(r, 1), TableFields.U(r, 2), TableFields.List(r, 7))).ToList());
        }

        private SC_OperationMiniGameInfo Info(uint uid, uint activityId)
        {
            (bool candy, List<Stage> stages) = Game(activityId);
            Dictionary<uint, Record> records = accountState.Get<Dictionary<uint, Record>>(uid, Key(activityId)) ?? [];
            bool Unlocked(Stage s) => s.Previous == 0 || (records.TryGetValue(s.Previous, out Record? p) && p.Cleared);
            SC_OperationMiniGameInfo reply = new() { IKDMNOBMIFE = activityId };
            if (candy)
            {
                IHNLKCLJMDC data = new();
                foreach (Stage s in stages)
                {
                    records.TryGetValue(s.Id, out Record? r);
                    data.KNMAKEJFGDK[s.Id] = new IHNLKCLJMDC.Types.NLEKECNKFOP { KNFOFFBOCML = r?.Cleared ?? false, MPNGHAKGLDM = Unlocked(s), CGFMBCCDFPG = r?.Best ?? 0, KNAHJGMNKIL = (uint)(r?.Stars.Count ?? 0) };
                }
                reply.CandyCatchData = data;
            }
            else
            {
                HGNOJKGKMNH data = new();
                foreach (Stage s in stages)
                {
                    records.TryGetValue(s.Id, out Record? r);
                    data.KNMAKEJFGDK[s.Id] = new HGNOJKGKMNH.Types.OCKOAIFFBNG { KNFOFFBOCML = r?.Cleared ?? false, MPNGHAKGLDM = Unlocked(s), CGFMBCCDFPG = r?.Best ?? 0, LELAOAJLKKI = r?.LastResult ?? 0, KNAHJGMNKIL = (uint)(r?.Stars.Count ?? 0) };
                }
                reply.CardFlipData = data;
            }

            return reply;
        }

        public override void HandleOperationMiniGameInfo(CS_OperationMiniGameInfo request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(Info(connection.Account.Uid, request.IKDMNOBMIFE));
        }

        public override void HandleOperationMiniGameStart(CS_OperationMiniGameStart request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_OperationMiniGameStart { IKDMNOBMIFE = request.IKDMNOBMIFE, StageId = request.StageId });
        }

        public void HandleOperationMiniGameFinish(CS_OperationMiniGameFinish request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            (bool candy, List<Stage> stages) = Game(request.IKDMNOBMIFE);
            Stage? stage = stages.FirstOrDefault(s => s.Id == request.StageId);
            List<IMessage> messages = [];
            Dictionary<uint, uint> granted = [];
            uint starsNow = 0;
            Record record;
            lock (AccountLock.For(uid))
            {
                Dictionary<uint, Record> records = accountState.Get<Dictionary<uint, Record>>(uid, Key(request.IKDMNOBMIFE)) ?? [];
                record = records.GetValueOrDefault(request.StageId) ?? new Record();
                record.Best = Math.Max(record.Best, request.Score);
                record.Cleared = true;
                if (request.CardFlip is { } flip)
                    record.LastResult = flip.Result;

                foreach (uint starId in stage?.Stars ?? [])
                {
                    IMessage? star = candy
                        ? tableService.GetTable<CandycatchStarData>().FirstOrDefault(r => TableFields.U(r, 1) == starId)
                        : tableService.GetTable<CardflipStarData>().FirstOrDefault(r => TableFields.U(r, 1) == starId);
                    if (star is null || TableFields.U(star, 4) > request.Score)
                        continue;
                    starsNow++;
                    if (record.Stars.Contains(starId))
                        continue;
                    record.Stars.Add(starId);
                    foreach ((uint item, uint count) in TableFields.Map(star, 3))
                    {
                        rewardService.Grant(uid, item, (int)count, messages);
                        granted[item] = granted.GetValueOrDefault(item) + count;
                    }
                }

                records[request.StageId] = record;
                accountState.Set(uid, Key(request.IKDMNOBMIFE), records);
            }

            SC_OperationMiniGameFinish reply = new() { IKDMNOBMIFE = request.IKDMNOBMIFE, StageId = request.StageId, Score = request.Score, CGFMBCCDFPG = record.Best };
            reply.UnlockStage.AddRange(stages.Where(s => s.Previous == request.StageId).Select(s => s.Id));
            if (candy)
            {
                SC_OperationMiniGameFinish.Types.GachaCounter counter = new() { KNAHJGMNKIL = starsNow };
                foreach ((uint item, uint count) in granted)
                    counter.Rewards[item] = count;
                reply.CandyCatch = counter;
            }
            else
            {
                SC_OperationMiniGameFinish.Types.GKIFHAMPMKP cards = new() { GJEIFFHNBOB = request.CardFlip?.GJEIFFHNBOB ?? 0, LELAOAJLKKI = record.LastResult, KNAHJGMNKIL = starsNow };
                foreach ((uint item, uint count) in granted)
                    cards.Rewards[item] = count;
                reply.CardFlip = cards;
            }

            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}
