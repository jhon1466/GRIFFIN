namespace Griffin.GameServer.Services.Darkov
{
    /// <summary>A quest of the account: its steps (0 = in progress, 1 = done, 3 = handed in) and whether the reward was taken.</summary>
    public sealed class DQuest
    {
        public Dictionary<uint, uint> Steps { get; set; } = [];
        public List<uint> Order { get; set; } = [];
        public bool Claimed { get; set; }
    }

    /// <summary>
    /// The quest chain of the mode (<c>EsQuestMain</c>: each quest has the steps of column 14 and the closing step of column 4, a reward in column 13 and the quest it follows in
    /// column 6). The client tables hold what each step asks for (<c>EsTargetCondition</c>, 27 kinds); here a step is completed when the player extracts from a room
    /// (A-145: the in-room objectives are not simulated one by one).
    /// </summary>
    public static class DarkovQuests
    {
        public const ushort Get = 10378, GetReply = 10379, Receive = 10380, ReceiveReply = 10381, Update = 10382, UpdateReply = 10383, Finish = 10384, FinishReply = 10385;

        public static PbW Write(uint id, DQuest quest)
        {
            var w = new PbW().V(1, id);
            foreach (uint step in quest.Order)
            {
                var s = new PbW().V(1, step);
                uint state = quest.Steps.GetValueOrDefault(step);
                if (state != 0)
                    s.V(2, state);
                w.M(2, s);
            }

            return w;
        }

        public static byte[] List(DarkovProfile profile)
        {
            var w = new PbW();
            foreach ((uint id, DQuest quest) in profile.Quests.Where(p => !p.Value.Claimed).OrderBy(p => p.Key))
                w.M(1, Write(id, quest));
            return w.ToArray();
        }

        /// <summary>The quest offered next: the first one of the chain whose predecessor is claimed.</summary>
        private static bool CanAccept(uint id, DarkovProfile profile, PbMsg row)
        {
            if (profile.Quests.ContainsKey(id))
                return false;
            foreach (ulong before in row.Nums(6))
                if (!profile.Quests.TryGetValue((uint)before, out DQuest? previous) || !previous.Claimed)
                    return false;
            return true;
        }

        public static (List<(ushort, byte[])>, bool) Handle(ushort id, PbMsg request, DarkovProfile profile)
        {
            uint quest = (uint)request.U(1);
            switch (id)
            {
                case Get:
                    return ([(GetReply, new PbW().B(1, List(profile)).ToArray())], false);
                case Receive:
                {
                    if (!RawTables.ById("EsQuestMainData").TryGetValue(quest, out PbMsg? row) || !CanAccept(quest, profile, row))
                        return ([(19900, new PbW().V(1, id).S(2, -10008).ToArray())], false);
                    var state = new DQuest();
                    foreach (string step in row.RawList(14).Select(System.Text.Encoding.UTF8.GetString))
                        if (uint.TryParse(step, out uint s))
                            state.Order.Add(s);
                    state.Order.Add((uint)row.U(4));
                    state.Order = state.Order.Distinct().ToList();
                    foreach (uint step in state.Order)
                        state.Steps[step] = 0;
                    profile.Quests[quest] = state;
                    return ([(ReceiveReply, new PbW().M(1, Write(quest, state)).ToArray())], true);
                }
                case Update:
                {
                    if (!profile.Quests.TryGetValue(quest, out DQuest? state) || !state.Steps.ContainsKey((uint)request.U(2)) || state.Steps[(uint)request.U(2)] == 0)
                        return ([(19900, new PbW().V(1, id).S(2, -10008).ToArray())], false);
                    state.Steps[(uint)request.U(2)] = 3;
                    return ([(UpdateReply, new PbW().M(1, Write(quest, state)).ToArray())], true);
                }
                default:
                {
                    if (!profile.Quests.TryGetValue(quest, out DQuest? state) || state.Claimed || state.Steps.Values.Any(v => v == 0)
                        || !RawTables.ById("EsQuestMainData").TryGetValue(quest, out PbMsg? row))
                        return ([(19900, new PbW().V(1, id).S(2, -10008).ToArray())], false);
                    state.Claimed = true;
                    var changed = new List<PbW>();
                    PbMsg? reward = row.Msg(13);
                    var items = reward?.Nums(1) ?? [];
                    var counts = reward?.Nums(2) ?? [];
                    var money = new List<uint>();
                    for (int i = 0; i < items.Count; i++)
                    {
                        uint cfg = (uint)items[i], amount = i < counts.Count ? (uint)counts[i] : 1;
                        if (cfg == 3041001 || cfg == 3041002 || cfg == 3041006)
                        {
                            profile.Ammo[cfg] = profile.Ammo.GetValueOrDefault(cfg) + amount;
                            money.Add(cfg);
                        }
                        else
                            DarkovHub.Give(profile, cfg, amount, changed);
                    }

                    var update = new PbW();
                    foreach (PbW slot in changed)
                        update.M(1, slot);
                    foreach (uint cfg in money)
                        update.M(2, new PbW().V(1, cfg).V(2, profile.Ammo.GetValueOrDefault(cfg)));
                    return ([(DarkovHub.ItemUpdate, update.ToArray()), (FinishReply, new PbW().V(1, quest).ToArray())], true);
                }
            }
        }

        /// <summary>A successful extraction completes the steps of the quests in progress.</summary>
        public static void CompleteOnExtraction(DarkovProfile profile)
        {
            foreach (DQuest quest in profile.Quests.Values.Where(q => !q.Claimed))
                foreach (uint step in quest.Order.ToList())
                    if (quest.Steps.GetValueOrDefault(step) == 0)
                        quest.Steps[step] = 1;
        }

        /// <summary>The quest block of a room update (component 14).</summary>
        public static PbW? Component(DarkovProfile profile)
        {
            var current = profile.Quests.Where(p => !p.Value.Claimed).OrderBy(p => p.Key).FirstOrDefault();
            return current.Value is null ? null : new PbW().M(1, Write(current.Key, current.Value));
        }
    }
}
