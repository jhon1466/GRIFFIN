using Griffin.Common.Networking;
using Griffin.Common.Proto;
using PcapDotNet.Packets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Griffin.GameServer.Handlers
{
    public sealed class CyclePlanHandler : CyclePlanHandlerBase
    {
        public override void HandleCyclePlan(CS_CyclePlan req, Connection connection)
        {
            // The installed 4.0.5136 CyclePlanData table parses successfully but has
            // no rows. Return an empty response for every requested category instead
            // of silently dropping types other than the one previously captured.
            connection.Send(new SC_CyclePlan { Type = req.Type });
        }
    }
}
