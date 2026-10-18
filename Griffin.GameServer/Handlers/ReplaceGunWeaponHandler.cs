using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class ReplaceGunWeaponHandler(IAccountStateService accountState) : ReplaceGunWeaponHandlerBase
    {
        /// <summary>CS: weapon id (1), skin id (2, 0 = none), slot of the accessory (3, 1-based). The skin is kept per weapon and slot and travels in the weapon list (A-89).</summary>
        public override void HandleReplaceGunWeaponModSkin(CS_ReplaceGunWeaponModSkin request, Connection connection)
        {
            if (connection.Account is not null && request.DPMKGPOCIOK is >= 1 and <= 16)
                WeaponModSkins.Set(accountState, connection.Account.Uid, request.Id, (int)request.DPMKGPOCIOK - 1, request.GunId);

            connection.Send(new SC_ReplaceGunWeaponModSkin
            {
                Id = request.Id,
                GunId = request.GunId,
                DPMKGPOCIOK = request.DPMKGPOCIOK,
            });
        }
    }
}
