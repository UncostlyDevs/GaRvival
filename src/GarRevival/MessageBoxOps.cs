using System.Collections.Generic;
using JunkWars.Common.Social;
using JunkWars.Common.Utils;

namespace GarRevival
{
    // The garage message box. The client only ever shows two kinds of entry
    // in it, friend invitations and guild invitations (EventMessageType), so
    // it starts empty. Whoever implements those adds to Messages and calls
    // Changed().
    internal static class MessageBoxOps
    {
        private const byte EvMessageBoxContentChanged = 167;

        public static readonly List<SocialMessageData> Messages = new List<SocialMessageData>();

        // Op 182. The client then asks for the friends' online status (183) itself.
        public static void Reply(Loopback.VirtualConnection conn, byte op)
        {
            GameServer.Reply(conn, op, new Dictionary<byte, object> { { 218, DataSerialization.ProtoSerializeObject(Messages) } });
        }

        // The client replaces its whole list with this one.
        public static void Changed(Loopback.VirtualConnection conn)
        {
            if (conn != null)
            {
                GameServer.Event(conn, EvMessageBoxContentChanged, new Dictionary<byte, object> { { 218, DataSerialization.ProtoSerializeObject(Messages) } });
            }
        }
    }
}
