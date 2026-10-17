using Griffin.Common.Table;
using Griffin.Database.Entities;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The three kinds of talent key the client keeps apart in <c>Gun</c>: private keys (field 42, three slots), public keys (field 43) and
    /// the PEAK keys (峰位键: <c>TalentKeyData</c> column 2 = 3, e.g. 921055 of Nikketa, the key of the Mind Spiral) which have their own slots in the
    /// MIDDLE of the wheel and travel in field 52 (<c>Gun.NJKHKDCNJPK</c>) and in the second list (field 3) of <c>SC_GunPrivateTalentSkillItemBelong2</c>.
    /// The request does not say which kind the item is, so the server decides by the table. ASSUMPTION A-61: until now every key was put in the
    /// private slots, so the peak key appeared on the side and not in the middle. The peak slots are saved in <c>GunTalentConsume</c> under
    /// reserved keys (like the core level), never sent to the client as consumption.
    /// </summary>
    public static class TalentKeySlots
    {
        public const int SlotCount = 3;

        /// <summary>Every reserved key of <c>GunTalentConsume</c> starts here (never an item id).</summary>
        public const uint ReservedBase = 0xFFFF0000;

        private const uint PeakBase = 0xFFFF0020;

        public static bool IsPeakKey(uint item)
        {
            if (item == 0 || TalentBits.Tables is null)
                return false;

            return TalentBits.Tables.GetTable<TalentKeyData>().Any(row => TableFields.U(row, 1) == item && TableFields.U(row, 2) == 3);
        }

        /// <summary>
        /// Moves the peak keys that an older build left in the private slots to the peak slots, and returns the three peak slots.
        /// </summary>
        public static uint[] Normalize(GunEntity gun)
        {
            uint[] peak = new uint[SlotCount];
            for (int i = 0; i < SlotCount; i++)
                peak[i] = gun.GunTalentConsume.GetValueOrDefault(PeakBase + (uint)i);

            bool changed = false;
            for (int i = 0; i < gun.PrivateTalentSkillItems.Length; i++)
            {
                uint item = gun.PrivateTalentSkillItems[i];
                if (item == 0 || !IsPeakKey(item))
                    continue;

                gun.PrivateTalentSkillItems[i] = 0;
                int free = Array.IndexOf(peak, 0u);
                if (free >= 0 && !peak.Contains(item))
                    peak[free] = item;
                changed = true;
            }

            if (changed)
                Store(gun, peak);
            return peak;
        }

        public static void Store(GunEntity gun, uint[] peak)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (peak[i] == 0)
                    gun.GunTalentConsume.Remove(PeakBase + (uint)i);
                else
                    gun.GunTalentConsume[PeakBase + (uint)i] = peak[i];
            }
        }
    }
}
