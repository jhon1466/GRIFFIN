using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class ReplaceGunWeaponHandler : ReplaceGunWeaponHandlerBase
    {
        public override void HandleReplaceGunWeaponModSkin(CS_ReplaceGunWeaponModSkin request, Connection connection)
        {
            connection.Send(new SC_ReplaceGunWeaponModSkin
            {
                Id = request.Id,
                GunId = request.GunId,
                DPMKGPOCIOK = request.DPMKGPOCIOK,
            });
        }
    }
}
