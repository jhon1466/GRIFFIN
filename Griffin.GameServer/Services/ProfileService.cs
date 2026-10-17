using Griffin.Common.Proto;
using Griffin.Database.Services;

namespace Griffin.GameServer.Services
{
    /// <summary>The player-card values the user can change (name, avatar, frame, motto, sex, birthday, medal, title).</summary>
    public sealed class ProfileState
    {
        public string? Name { get; set; }
        public uint? Portrait { get; set; }
        public uint? PortraitFrame { get; set; }
        public string? Motto { get; set; }
        public int? Sex { get; set; }
        public uint? Birthday { get; set; }
        public bool? BirthdayHidden { get; set; }
        public uint? Medal { get; set; }
        public uint? Title { get; set; }
        public uint? BackgroundCard { get; set; }
        /// <summary>Characters shown on the player card, in order: (character, costume).</summary>
        public List<DisplayGun>? Display { get; set; }
    }

    public sealed class DisplayGun
    {
        public uint GunId { get; set; }
        public uint CostumeId { get; set; }
    }

    /// <summary>
    /// Persists the player-card changes in <c>AccountStates</c> (key <c>profile</c>) and overlays them on the otherwise captured
    /// <c>SC_Login.User</c>, so a change made in the profile screen is still there after logging in again.
    /// </summary>
    public interface IProfileService : IGameService
    {
        ProfileState Get(uint accountUid);

        void Update(uint accountUid, Action<ProfileState> change);

        void ApplyTo(uint accountUid, User user);

        /// <summary>The player-card showcase built from the owned characters (level, grade, costume, affection level).</summary>
        List<HLDHKCBIDLJ> BuildDisplay(uint accountUid);
    }

    public sealed class ProfileService(IAccountStateService accountState, IAccountService accountService) : IProfileService
    {
        private const string Key = "profile";

        public ProfileState Get(uint accountUid) => accountState.Get<ProfileState>(accountUid, Key) ?? new ProfileState();

        public void Update(uint accountUid, Action<ProfileState> change)
        {
            ProfileState state = Get(accountUid);
            change(state);
            accountState.Set(accountUid, Key, state);
        }

        public void ApplyTo(uint accountUid, User user)
        {
            ProfileState state = Get(accountUid);

            if (!string.IsNullOrEmpty(state.Name)) user.Name = state.Name;
            if (state.Portrait is { } portrait) user.Portrait = portrait;
            if (state.PortraitFrame is { } frame) user.PortraitFrame = frame;
            if (state.Motto is { } motto) user.Motto = motto;
            if (state.Sex is { } sex) user.Sex = (Sex)sex;
            if (state.Birthday is { } birthday) user.Birthday = birthday;
            if (state.Medal is { } medal) user.Medal = medal;
            if (state.Title is { } title) user.Title = title;
            if (state.Display is not null)
            {
                user.DELPACHDBFG.Clear();
                user.DELPACHDBFG.AddRange(BuildDisplay(accountUid));
            }
        }

        public List<HLDHKCBIDLJ> BuildDisplay(uint accountUid)
        {
            List<HLDHKCBIDLJ> display = [];
            var guns = accountService.GetByUid(accountUid)?.Guns;
            int index = 0;
            foreach (DisplayGun shown in Get(accountUid).Display ?? [])
            {
                var gun = guns?.FirstOrDefault(g => g.GunId == shown.GunId);
                if (gun is null)
                    continue;

                display.Add(new HLDHKCBIDLJ
                {
                    Idx = index++,
                    KKNFMCGIFGI = new GunAvatar
                    {
                        Id = gun.GunId,
                        Level = (uint)gun.Level,
                        Grade = gun.Grade,
                        CostumeId = shown.CostumeId != 0 ? shown.CostumeId : gun.CostumeId,
                    },
                    KFIAKLNJHMB = gun.LoveLevel,
                });
            }

            return display;
        }
    }
}
