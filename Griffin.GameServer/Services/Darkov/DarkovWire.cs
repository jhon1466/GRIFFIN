namespace Griffin.GameServer.Services.Darkov
{
    /// <summary>The wire form of the account data of the mode (backpack, storage, hero) shared by the hub answers and the start of a room.</summary>
    public static class DarkovWire
    {
        public static PbW Backpack(DarkovProfile profile)
        {
            var bag = new PbW();
            var slots = new PbW().V(1, (ulong)profile.Bag.Count);
            foreach (DItem? item in profile.Bag)
                slots.M(2, item is null ? new PbW() : new PbW().M(1, item.Write()));
            bag.M(1, slots);
            bag.M(2, new PbW().V(1, 1).M(2, new PbW()));
            bag.M(3, new PbW().Packed(2, profile.QuickSlots.Select(q => (ulong)q)));
            foreach (DItem? item in profile.Equip)
                bag.M(4, item is null ? new PbW() : item.Write());
            foreach ((uint cfg, uint count) in profile.Ammo.OrderByDescending(p => p.Key))
                bag.M(5, new PbW().V(1, cfg).V(2, count));
            return bag;
        }

        public static PbW Stash(DarkovProfile profile)
        {
            var grid = new PbW().V(1, (ulong)profile.Stash.Count);
            foreach (DItem? item in profile.Stash)
                grid.M(2, item is null ? new PbW() : new PbW().M(1, item.Write()));
            return new PbW().M(1, grid);
        }

        /// <summary><c>SC_GetDarkovInfo</c>: storage, backpack, heroes, progress and the stages the account may enter.</summary>
        public static byte[] Info(DarkovProfile profile)
        {
            ulong heroUnit = 1877465422351060992;
            var w = new PbW().M(1, new PbW().V(4, 2)).M(2, Stash(profile)).M(3, Backpack(profile));
            w.M(5, new PbW().V(1, profile.Hero)
                .M(2, new PbW().V(1, heroUnit).V(2, 1010090).V(3, 1).M(12, new PbW().V(1, profile.HeroHp).V(2, 45000).V(3, 1).M(6, new PbW()).M(6, new PbW())))
                .V(7, heroUnit).M(8, new PbW()).M(8, new PbW()).M(8, new PbW()).M(8, new PbW()).V(12, 1));
            w.M(6, new PbW().V(1, profile.Exp).V(3, 1));
            w.V(7, 29460);
            w.M(8, new PbW().Packed(1, [profile.Hero, 0, 0]).V(2, 3230000));
            foreach (uint stage in profile.UnlockedStages)
                w.M(10, new PbW().V(1, stage).M(2, new PbW().V(1, 1)));
            w.M(11, DarkovHub.Workbench(profile));
            w.M(13, new PbW());
            w.M(14, new PbW().Packed(1, [9964]));
            w.V(18, profile.CurrentPoi);
            w.M(19, new PbW().V(1, 1001003).V(2, 0));
            return w.ToArray();
        }
    }
}
