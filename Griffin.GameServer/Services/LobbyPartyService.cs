using Griffin.Common.Proto;
using Griffin.Common.Table;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The lobby parties (影像图室 &gt; 派对: 星光盛宴 101, 枕畔梦宴 102, 丰盈庆典 103, 拾光筑愿 104). A party is a list of sections
    /// (<c>LobbyPartySectionBySectionGroupData</c>); a section is a graph of nodes (<c>LobbyTaskNodeByNodeGroupData</c> → <c>LobbyTaskNodeData</c>: col 2 = nodes
    /// that must be done first, col 6 = type 1 plot, 2 dialogue, 3 level, 4 interaction, 6 photo, 7 furniture, 8 door) that the client plays inside the party room; when
    /// a node is played the client sends <c>CS_LobbyPartyNodeCommit</c>. The server keeps the progress, tells the client which nodes of the current section are open
    /// and which are done, pays the rewards of a finished section (<c>LobbyPartySectionData</c> col 10) and opens the next one. ASSUMPTION A-128 (docs/assumptions.md).
    /// </summary>
    public interface ILobbyPartyService : IGameService
    {
        SC_LobbyPartyInfo BuildInfo(uint uid);

        /// <summary>
        /// The client uses one request for two things: with only the party id it starts or continues the mission (<paramref name="reviewSection"/> = 0); with a finished
        /// section it replays that section ("review"). The party becomes the current one and the info the client re-reads on entering the room is returned.
        /// </summary>
        SC_LobbyPartyInfo Join(uint uid, uint partyId, uint reviewSection);

        void Leave(uint uid);

        /// <summary>A node was played: marks it, opens the next ones and pays a finished section. False when the node cannot be committed.</summary>
        bool Commit(uint uid, uint partyId, uint sectionId, uint nodeId, List<Google.Protobuf.IMessage> messages, out SC_LobbyPartyNodeCommit? reply);

        void SaveAvgSelect(uint uid, uint partyId, uint avgId, uint choice);
    }

    public sealed class LobbyPartyService(ITableService tables, IAccountStateService accountState, IRewardService rewardService) : ILobbyPartyService
    {
        private const string Key = "lobby.party";

        public sealed class PartyProgress
        {
            public uint Section { get; set; }
            public List<uint> Done { get; set; } = [];
            public List<uint> CompletedSections { get; set; } = [];
            public bool Finished { get; set; }
        }

        public sealed class PartyState
        {
            public uint Joined { get; set; }
            public uint ReviewSection { get; set; }
            public int Version { get; set; }
            public Dictionary<string, PartyProgress> Parties { get; set; } = [];
            public Dictionary<string, Dictionary<uint, uint>> Avg { get; set; } = [];
        }

        /// <summary>
        /// Version 2: parties 101-103 start with no progress like 104 (the first build of the service showed them as finished, with the album check, although the
        /// player had not played them; nothing could have finished them in that build, so every saved progress of those parties is dropped).
        /// </summary>
        private PartyState Load(uint uid)
        {
            PartyState state = accountState.Get<PartyState>(uid, Key) ?? new PartyState();
            if (state.Version < 2)
            {
                foreach (string party in new[] { "101", "102", "103" })
                    state.Parties.Remove(party);
                state.Version = 2;
            }

            return state;
        }

        private void Save(uint uid, PartyState state) => accountState.Set(uid, Key, state);

        private IEnumerable<T> Rows<T>() where T : Google.Protobuf.IMessage<T>, new() => tables.GetTable<T>();

        // ---------------------------------------------------------------- tables

        private List<uint> PartyIds() => Rows<LobbyPartyData>().Select(row => row.Id).OrderBy(id => id).ToList();

        /// <summary>The sections of a party in play order (<c>LobbyPartySectionBySectionGroupData</c>).</summary>
        private List<uint> SectionsOf(uint party)
        {
            LobbyPartySectionBySectionGroupData? group = Rows<LobbyPartySectionBySectionGroupData>().FirstOrDefault(row => TableFields.U(row, 1) == party);
            return group is null ? [] : TableFields.List(group, 2).ToList();
        }

        private List<uint> NodesOf(uint section)
        {
            LobbyPartySectionData? row = Rows<LobbyPartySectionData>().FirstOrDefault(r => r.SectionId == section);
            if (row is null)
                return [];
            uint group = TableFields.U(row, 9);
            LobbyTaskNodeByNodeGroupData? nodes = Rows<LobbyTaskNodeByNodeGroupData>().FirstOrDefault(r => TableFields.U(r, 1) == group);
            return nodes is null ? [] : TableFields.List(nodes, 2).ToList();
        }

        private Dictionary<uint, LobbyTaskNodeData> NodeRows(IEnumerable<uint> ids)
        {
            HashSet<uint> wanted = ids.ToHashSet();
            return Rows<LobbyTaskNodeData>().Where(r => wanted.Contains(TableFields.U(r, 1))).ToDictionary(r => TableFields.U(r, 1));
        }

        /// <summary>Open nodes of a section: not done and every node of col 2 (the nodes that come first) done.</summary>
        private List<uint> OpenNodes(uint section, IReadOnlyCollection<uint> done)
        {
            List<uint> all = NodesOf(section);
            Dictionary<uint, LobbyTaskNodeData> rows = NodeRows(all);
            return all.Where(id => !done.Contains(id) && rows.TryGetValue(id, out LobbyTaskNodeData? row)
                && TableFields.List(row, 2).Where(pre => pre != 0).All(done.Contains)).ToList();
        }

        // ---------------------------------------------------------------- state

        /// <summary>Progress of a party, created on first use: the opening parties (101-103) are finished, the others start on their first section.</summary>
        private PartyProgress ProgressOf(PartyState state, uint party)
        {
            if (state.Parties.TryGetValue(party.ToString(), out PartyProgress? known))
                return known;

            List<uint> sections = SectionsOf(party);
            var progress = new PartyProgress { Section = sections.FirstOrDefault() };
            state.Parties[party.ToString()] = progress;
            return progress;
        }

        private KEAKDAMCBMM StateOf(PartyState state, uint party)
        {
            PartyProgress progress = ProgressOf(state, party);
            var message = new KEAKDAMCBMM { Id = party, AACANKHENII = progress.Finished };
            foreach (uint section in progress.CompletedSections)
            {
                var done = new BNNMKBFFNHJ { SectionId = section, BAJHKMIBPDC = true };
                foreach (uint node in NodesOf(section))
                    done.LGNOAHMHJHH[node] = new CIFKOJJBCHF { Id = node, BAJHKMIBPDC = true };
                message.HFKNLIGCIBP[section] = done;
            }

            if (!progress.Finished && progress.Section != 0)
            {
                var current = new BNNMKBFFNHJ { SectionId = progress.Section };
                foreach (uint node in progress.Done)
                    current.LGNOAHMHJHH[node] = new CIFKOJJBCHF { Id = node, BAJHKMIBPDC = true };
                foreach (uint node in OpenNodes(progress.Section, progress.Done))
                    current.LDLCBHAEFHO.Add(new CIFKOJJBCHF { Id = node });
                message.CGCCNPFFNHA = current;
            }

            if (state.Avg.TryGetValue(party.ToString(), out Dictionary<uint, uint>? choices) && choices.Count > 0)
            {
                message.MLIJIDKIFGF = new KEMPBIAFGHB();
                foreach ((uint avg, uint choice) in choices)
                    message.MLIJIDKIFGF.HFJLFAEFENH[avg] = choice;
            }

            return message;
        }

        public SC_LobbyPartyInfo BuildInfo(uint uid)
        {
            lock (AccountLock.For(uid))
            {
                PartyState state = Load(uid);
                var info = new SC_LobbyPartyInfo();
                foreach (uint party in PartyIds())
                    info.AGCMHJFOGIH[party] = StateOf(state, party);
                // The client takes ONE of two branches (ILMOIPFAOCF / FBOJJECAPOK): the mission (current party id, nodes from the state of the party; the join message must be
                // absent) or a review of a finished section (no current party id; the join message lists the nodes to play). Sending both left an empty room (A-129).
                if (state.Joined != 0 && info.AGCMHJFOGIH.ContainsKey(state.Joined))
                {
                    if (state.ReviewSection == 0)
                        info.LKIBNJBDDJI = state.Joined;
                    else
                    {
                        info.DOGDHOGHFNH = new FCGCLLGILIJ { Id = state.Joined, SectionId = state.ReviewSection };
                        info.DOGDHOGHFNH.LHPFELEOEKI.AddRange(NodesOf(state.ReviewSection));
                    }
                }

                Save(uid, state);
                return info;
            }
        }

        public SC_LobbyPartyInfo Join(uint uid, uint partyId, uint reviewSection)
        {
            lock (AccountLock.For(uid))
            {
                PartyState state = Load(uid);
                if (PartyIds().Contains(partyId))
                {
                    state.Joined = partyId;
                    PartyProgress progress = ProgressOf(state, partyId);
                    state.ReviewSection = reviewSection != 0 && (progress.CompletedSections.Contains(reviewSection) || progress.Finished) ? reviewSection : 0;
                }

                Save(uid, state);
            }

            return BuildInfo(uid);
        }

        public void Leave(uint uid)
        {
            lock (AccountLock.For(uid))
            {
                PartyState state = Load(uid);
                if (state.Joined != 0)
                {
                    state.Joined = 0;
                    state.ReviewSection = 0;
                    Save(uid, state);
                }
            }
        }

        public bool Commit(uint uid, uint partyId, uint sectionId, uint nodeId, List<Google.Protobuf.IMessage> messages, out SC_LobbyPartyNodeCommit? reply)
        {
            reply = null;
            lock (AccountLock.For(uid))
            {
                PartyState state = Load(uid);
                if (!PartyIds().Contains(partyId))
                    return false;
                PartyProgress progress = ProgressOf(state, partyId);
                var rewards = new Dictionary<uint, uint>();
                bool sectionDone = false;
                // The client counts the commits it sends and only goes on when each is answered, and it also commits the optional nodes it completes by itself, so a node
                // of the current section is accepted even if the server would not have opened it yet (A-129); a node that is not of the section is refused.
                if (!progress.Finished && progress.Section == sectionId && !progress.Done.Contains(nodeId))
                {
                    if (!NodesOf(sectionId).Contains(nodeId))
                        return false;

                    progress.Done.Add(nodeId);
                    if (NodesOf(sectionId).All(progress.Done.Contains))
                    {
                        sectionDone = true;
                        LobbyPartySectionData? row = Rows<LobbyPartySectionData>().FirstOrDefault(r => r.SectionId == sectionId);
                        if (row is not null)
                            foreach ((uint item, uint count) in TableFields.Map(row, 10))
                            {
                                rewardService.Grant(uid, item, (int)count, messages);
                                rewards[item] = rewards.GetValueOrDefault(item) + count;
                            }

                        progress.CompletedSections.Add(sectionId);
                        progress.Done = [];
                        List<uint> sections = SectionsOf(partyId);
                        int index = sections.IndexOf(sectionId);
                        if (index >= 0 && index + 1 < sections.Count)
                            progress.Section = sections[index + 1];
                        else
                        {
                            progress.Section = 0;
                            progress.Finished = true;
                        }

                        Log.Information("Party: account {Uid} finished section {Section} of party {Party}{Last}.", uid, sectionId, partyId, progress.Finished ? " (the party is complete)" : "");
                    }
                }
                else if (!(progress.CompletedSections.Contains(sectionId) || progress.Done.Contains(nodeId)))
                {
                    return false;
                }

                Save(uid, state);
                reply = new SC_LobbyPartyNodeCommit { NDMBKBDIIEG = StateOf(state, partyId), NFBGCJPJMPJ = sectionDone };
                foreach ((uint item, uint count) in rewards)
                    reply.GEINLPLCKNP[item] = count;
                return true;
            }
        }

        public void SaveAvgSelect(uint uid, uint partyId, uint avgId, uint choice)
        {
            lock (AccountLock.For(uid))
            {
                PartyState state = Load(uid);
                if (!state.Avg.TryGetValue(partyId.ToString(), out Dictionary<uint, uint>? choices))
                    state.Avg[partyId.ToString()] = choices = [];
                choices[avgId] = choice;
                Save(uid, state);
            }
        }
    }
}
