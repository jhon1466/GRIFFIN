using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Database.Services;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// The characters shown on the player card (<c>CS_DisplaySetting</c>: (character, costume) pairs). Only owned characters can be
    /// shown, without repeats and at most 15 (the real client sends a 15-entry list in which empty slots are empty entries; with a limit of 5 it was refused); the list is stored with the profile and is
    /// also part of <c>SC_Login.User</c>.
    /// </summary>
    public sealed class DisplaySettingHandler(IProfileService profile, IAccountService accountService) : DisplaySettingHandlerBase
    {
        private const int Slots = 15;

        public override void HandleDisplaySetting(CS_DisplaySetting request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            List<uint> owned = accountService.GetByUid(uid)?.Guns.Select(g => g.GunId).ToList() ?? [];
            var wanted = request.KODBGINIGOL.Where(w => w.GunId != 0).ToList();
            if (request.KODBGINIGOL.Count > Slots || wanted.Count > Slots || wanted.Select(w => w.GunId).Distinct().Count() != wanted.Count || wanted.Any(w => !owned.Contains(w.GunId)))
            {
                connection.Refuse(MsgId.MsgCsDisplaySetting, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            profile.Update(uid, state => state.Display = wanted.Select(w => new DisplayGun { GunId = w.GunId, CostumeId = w.CostumeId }).ToList());
            SC_DisplaySetting response = new();
            response.DELPACHDBFG.AddRange(profile.BuildDisplay(uid));
            connection.Send(response);
        }
    }
}
