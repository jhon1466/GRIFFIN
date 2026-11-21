using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// The combat tutorial (猎人评定 / Hunter Assessment, SimCombatTutorial*): sections (SimCombatTutorialSectionData, column 3 = its chapters)
    /// hold chapters (SimCombatTutorialChapterData: column 5 = stages, column 10 = reward once all of them are cleared); the progress rows
    /// (SimCombatTutorialProgressData, column 3 = reward) pay as the account goes on. The client asks the chapter reward right after the last
    /// stage of a chapter: without an answer the result screen stayed frozen (A-234). Info: field 1 = current section, 2 = sections whose
    /// reward was taken, 3 = chapters whose reward was taken.
    /// </summary>
    public sealed class SimCombatTutorialHandler(ITableService tableService, IAccountStateService accountState, IRewardService rewardService) : SimCombatTutorialHandlerBase
    {
        private const string StateKey = "simcombat.tutorial.v1";

        public sealed class State
        {
            public HashSet<uint> Chapters { get; set; } = [];
            public HashSet<uint> Sections { get; set; } = [];
        }

        private State Load(uint uid) => accountState.Get<State>(uid, StateKey) ?? new State();

        private HashSet<uint> Cleared(uint uid) =>
            AchievementService.ClearedStages(accountState.Get<Dictionary<string, StageRecord>>(uid, "stage.records") ?? []);

        private IReadOnlyList<uint> StagesOf(IMessage chapter) => TableFields.List(chapter, 5);

        private bool ChapterDone(IMessage chapter, HashSet<uint> cleared) => StagesOf(chapter).Count > 0 && StagesOf(chapter).All(cleared.Contains);

        /// <summary>The first section with a chapter not cleared yet (the last one when everything is done).</summary>
        private uint CurrentSection(HashSet<uint> cleared)
        {
            Dictionary<uint, IMessage> chapters = tableService.GetTable<SimCombatTutorialChapterData>().Cast<IMessage>().ToDictionary(r => TableFields.U(r, 1));
            List<IMessage> sections = tableService.GetTable<SimCombatTutorialSectionData>().Cast<IMessage>().OrderBy(r => TableFields.U(r, 1)).ToList();
            foreach (IMessage section in sections)
            {
                IReadOnlyList<uint> ids = TableFields.List(section, 3);
                if (ids.Any(id => chapters.TryGetValue(id, out IMessage? c) && !ChapterDone(c, cleared)))
                    return TableFields.U(section, 1);
            }
            return sections.Count == 0 ? 0 : TableFields.U(sections[^1], 1);
        }

        private SC_SimCombatTutorialInfoV2 Info(uint uid)
        {
            State state = Load(uid);
            PAHCFJFLGII tutorial = new() { NPMHOABEFGB = CurrentSection(Cleared(uid)) };
            tutorial.AECOEJPLBFD.AddRange(state.Sections.OrderBy(x => x));
            tutorial.GKALMFPOIFP.AddRange(state.Chapters.OrderBy(x => x));
            return new SC_SimCombatTutorialInfoV2 { Tutorial = tutorial };
        }

        public override void HandleSimCombatTutorialInfoV2(CS_SimCombatTutorialInfoV2 request, Connection connection)
        {
            if (connection.Account is null)
            {
                connection.Send(new SC_SimCombatTutorialInfoV2 { Tutorial = new PAHCFJFLGII() });
                return;
            }
            connection.Send(Info(connection.Account.Uid));
        }

        /// <summary>The reward of a chapter whose stages are all cleared (column 10), once.</summary>
        public override void HandleSimCombatTutorialChapterGetRewardV2(CS_SimCombatTutorialChapterGetRewardV2 request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                IMessage? chapter = tableService.GetTable<SimCombatTutorialChapterData>().Cast<IMessage>().FirstOrDefault(r => TableFields.U(r, 1) == request.ChapterId);
                State state = Load(uid);
                if (chapter is null || !ChapterDone(chapter, Cleared(uid)))
                {
                    connection.Refuse(MsgId.MsgCsSimCombatTutorialChapterGetRewardV2, HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                List<IMessage> messages = [];
                if (state.Chapters.Add(request.ChapterId))
                {
                    foreach ((uint item, uint count) in TableFields.Map(chapter, 10))
                        if (count > 0)
                            rewardService.Grant(uid, item, (int)count, messages);
                    accountState.Set(uid, StateKey, state);
                }
                messages.Add(new SC_SimCombatTutorialChapterGetRewardV2 { ChapterId = request.ChapterId });
                messages.Add(new SC_SimCombatTutorialSectionChange { NPMHOABEFGB = CurrentSection(Cleared(uid)) });
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        /// <summary>A progress reward (SimCombatTutorialProgressData column 3) or a section reward, once.</summary>
        public override void HandleSimCombatTutorialSectionGetRewardV2(CS_SimCombatTutorialSectionGetRewardV2 request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                State state = Load(uid);
                IMessage? progress = tableService.GetTable<SimCombatTutorialProgressData>().Cast<IMessage>().FirstOrDefault(r => TableFields.U(r, 1) == request.Id);
                if (progress is null)
                {
                    connection.Refuse(MsgId.MsgCsSimCombatTutorialSectionGetRewardV2, HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                List<IMessage> messages = [];
                if (state.Sections.Add(request.Id))
                {
                    foreach ((uint item, uint count) in TableFields.Map(progress, 3))
                        if (count > 0)
                            rewardService.Grant(uid, item, (int)count, messages);
                    accountState.Set(uid, StateKey, state);
                }
                messages.Add(new SC_SimCombatTutorialSectionGetRewardV2 { Id = request.Id });
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }
    }
}
