namespace Nativra.X86.Loader
{
    /// <summary>Converts sampled XInput state into one key transition per poll.</summary>
    public sealed class XInputKeystrokes
    {
        private static readonly ushort[] Masks =
            { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 4096, 8192, 16384, 32768 };
        private static readonly ushort[] Keys =
            { 0x5810, 0x5811, 0x5812, 0x5813, 0x5814, 0x5815, 0x5816, 0x5817,
              0x5805, 0x5804, 0x5800, 0x5801, 0x5802, 0x5803 };
        private ushort buttons;
        private bool leftTrigger, rightTrigger;
        private ushort leftStick, rightStick;

        public bool Poll(ushort currentButtons, byte left, byte right,
            short lx, short ly, short rx, short ry, out ushort key, out ushort flags)
        {
            for (var i = 0; i < Masks.Length; i++)
            {
                var mask = Masks[i];
                if (((buttons ^ currentButtons) & mask) == 0) continue;
                buttons ^= mask;
                key = Keys[i];
                flags = (ushort)((currentButtons & mask) != 0 ? 1 : 2);
                return true;
            }
            if (Trigger(left > 30, ref leftTrigger, 0x5806, out key, out flags) ||
                Trigger(right > 30, ref rightTrigger, 0x5807, out key, out flags)) return true;
            return Stick(Direction(lx, ly, 0x5820), ref leftStick, out key, out flags) ||
                Stick(Direction(rx, ry, 0x5830), ref rightStick, out key, out flags);
        }

        private static bool Trigger(bool current, ref bool previous, ushort value,
            out ushort key, out ushort flags)
        {
            key = flags = 0;
            if (current == previous) return false;
            previous = current;
            key = value;
            flags = (ushort)(current ? 1 : 2);
            return true;
        }

        private static ushort Direction(short x, short y, ushort basis)
        {
            var horizontal = x > 20000 ? 1 : x < -20000 ? -1 : 0;
            var vertical = y > 20000 ? 1 : y < -20000 ? -1 : 0;
            if (vertical == 0) return horizontal == 0 ? (ushort)0 : (ushort)(basis + (horizontal > 0 ? 2 : 3));
            if (vertical > 0) return (ushort)(basis + (horizontal == 0 ? 0 : horizontal > 0 ? 5 : 4));
            return (ushort)(basis + (horizontal == 0 ? 1 : horizontal > 0 ? 6 : 7));
        }

        private static bool Stick(ushort current, ref ushort previous, out ushort key, out ushort flags)
        {
            key = flags = 0;
            if (current == previous) return false;
            // Release the old direction before pressing a different one.
            key = previous != 0 ? previous : current;
            flags = (ushort)(previous != 0 ? 2 : 1);
            previous = previous != 0 ? (ushort)0 : current;
            return true;
        }
    }
}
