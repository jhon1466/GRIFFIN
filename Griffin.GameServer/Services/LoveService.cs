using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Entities;

namespace Griffin.GameServer.Services
{
    /// <summary>One affection event of a character (<c>LoveEventData</c>): a demand to give (type 1) or a together-battle stage (type 2).</summary>
    public sealed record LoveEvent(uint Id, uint Suffix, uint Type, IReadOnlyList<(uint Item, uint Count)> Demands, uint Stage);

    /// <summary>
    /// The affection (好感 / 契合度) ladder. In the client the experience bar of a level only fills: when it is full the level does NOT rise by
    /// itself, the panel switches to "event mode" (<c>Love.f3</c> = the event suffix of <c>LoveLevelData</c> column 3, 101..104 = demand, 201..203 =
    /// together-battle) and the level rises when the event is completed (the demand is paid with <c>CS_LoveSendEventGift</c>, the battle is won).
    /// ASSUMPTION A-55: the pending event is derived (level 1..7 with the experience of the level complete), nothing extra is stored; the demand is the
    /// <c>LoveEventData</c> column 6 list; "upgrade directly" completes the pending event for free; level 8 (suffix 9999, the vow) keeps the old flow.
    /// </summary>
    public interface ILoveService : IGameService
    {
        uint Cap { get; }

        long Threshold(uint level);

        /// <summary>Adds experience: it fills the bar of the current level and stops there (the event decides the next level).</summary>
        void AddExperience(GunEntity gun, long experience);

        /// <summary>The event the character waits for (level 1..7 with a full bar), null when none.</summary>
        LoveEvent? Pending(GunEntity gun);

        /// <summary>Raises the level by one (the pending event is done) and pays the reward of the new level.</summary>
        void Advance(uint uid, GunEntity gun, List<IMessage> messages);
    }

    public sealed class LoveService(ITableService tableService, IRewardService rewardService) : ILoveService
    {
        private const uint VowEvent = 9999;

        public uint Cap => tableService.GetTable<LoveLevelData>().Where(l => TableFields.U(l, 2) > 0).Select(l => TableFields.U(l, 1)).DefaultIfEmpty(8u).Max();

        public long Threshold(uint level) =>
            tableService.GetTable<LoveLevelData>().FirstOrDefault(l => TableFields.U(l, 1) == level) is { } row ? TableFields.U(row, 2) : long.MaxValue;

        public void AddExperience(GunEntity gun, long experience)
        {
            uint level = gun.LoveLevel == 0 ? 1 : gun.LoveLevel;
            gun.LoveLevel = level;
            gun.LoveExp = Math.Min(gun.LoveExp + experience, Threshold(Math.Min(level, Cap)));
        }

        public LoveEvent? Pending(GunEntity gun)
        {
            uint level = gun.LoveLevel == 0 ? 1 : gun.LoveLevel;
            if (level >= Cap || gun.LoveExp < Threshold(level))
                return null;

            IMessage? levelRow = tableService.GetTable<LoveLevelData>().FirstOrDefault(l => TableFields.U(l, 1) == level);
            uint suffix = levelRow is null ? 0 : TableFields.U(levelRow, 3);
            if (suffix == 0 || suffix == VowEvent)
                return null;

            IMessage? row = tableService.GetTable<LoveEventData>().FirstOrDefault(e => TableFields.U(e, 2) == gun.GunId && TableFields.U(e, 3) == suffix);
            return row is null ? null : new LoveEvent(TableFields.U(row, 1), suffix, TableFields.U(row, 4), TableFields.Map(row, 6).ToList(), TableFields.U(row, 7));
        }

        public void Advance(uint uid, GunEntity gun, List<IMessage> messages)
        {
            uint level = gun.LoveLevel == 0 ? 1 : gun.LoveLevel;
            gun.LoveLevel = Math.Min(level + 1, Cap);
            for (uint reached = level + 1; reached <= gun.LoveLevel; reached++)
            {
                IMessage? row = tableService.GetTable<LoveLevelData>().FirstOrDefault(l => TableFields.U(l, 1) == reached);
                if (row is null)
                    continue;

                foreach ((uint item, uint count) in TableFields.Map(row, 7))
                    rewardService.Grant(uid, item, (int)count, messages);
            }
        }
    }
}
