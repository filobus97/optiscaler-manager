// OptiScaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.Buffers.Binary;
using System.Linq;
using OptiscalerManager.Core.Input;
using Xunit;

namespace OptiscalerManager.Core.Tests
{
    /// <summary>
    /// The controller mapping is pure logic fed by raw evdev frames, so the whole
    /// decode path is verifiable without a physical pad.
    /// </summary>
    public class GamepadDecoderTests
    {
        /// <summary>Builds a real 24-byte struct input_event as the kernel writes it.</summary>
        private static byte[] Frame(ushort type, ushort code, int value)
        {
            var buf = new byte[Evdev.EventSize];
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0, 8), 1_700_000_000); // tv_sec
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(8, 8), 0);             // tv_usec
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16, 2), type);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(18, 2), code);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(20, 4), value);
            return buf;
        }

        [Fact]
        public void ParsesRawFrame_FromTheWire()
        {
            var d = new EvdevGamepadDecoder();
            var changes = d.Feed(Frame(Evdev.EV_KEY, Evdev.BTN_SOUTH, 1));
            Assert.Equal(new[] { new GamepadInput(GamepadAction.Accept, true) }, changes);
        }

        [Theory]
        [InlineData(Evdev.BTN_SOUTH, GamepadAction.Accept)]   // A
        [InlineData(Evdev.BTN_EAST, GamepadAction.Cancel)]    // B
        [InlineData(Evdev.BTN_START, GamepadAction.Accept)]
        [InlineData(Evdev.BTN_SELECT, GamepadAction.Cancel)]
        [InlineData(Evdev.BTN_TR, GamepadAction.NextSection)]
        [InlineData(Evdev.BTN_TL, GamepadAction.PrevSection)]
        public void MapsFaceAndShoulderButtons(ushort code, GamepadAction expected)
            => Assert.Equal(expected, EvdevGamepadDecoder.ButtonAction(code));

        [Fact]
        public void IgnoresButtonsWeDoNotUse()
            => Assert.Equal(GamepadAction.None, EvdevGamepadDecoder.ButtonAction(0x13c)); // BTN_MODE (guide)

        [Fact]
        public void ButtonPressThenRelease_EmitsBothTransitions()
        {
            var d = new EvdevGamepadDecoder();
            Assert.Equal(new[] { new GamepadInput(GamepadAction.Accept, true) },
                d.Feed(Evdev.EV_KEY, Evdev.BTN_SOUTH, 1));
            Assert.Equal(new[] { new GamepadInput(GamepadAction.Accept, false) },
                d.Feed(Evdev.EV_KEY, Evdev.BTN_SOUTH, 0));
        }

        [Fact]
        public void KernelAutoRepeat_IsIgnored_SoWeControlOurOwnRepeat()
        {
            var d = new EvdevGamepadDecoder();
            d.Feed(Evdev.EV_KEY, Evdev.BTN_SOUTH, 1);
            Assert.Empty(d.Feed(Evdev.EV_KEY, Evdev.BTN_SOUTH, 2)); // value 2 == kernel repeat
        }

        [Theory]
        [InlineData(Evdev.ABS_HAT0Y, -1, GamepadAction.Up)]
        [InlineData(Evdev.ABS_HAT0Y, 1, GamepadAction.Down)]
        [InlineData(Evdev.ABS_HAT0X, -1, GamepadAction.Left)]
        [InlineData(Evdev.ABS_HAT0X, 1, GamepadAction.Right)]
        public void DpadMapsToDirections(ushort code, int value, GamepadAction expected)
        {
            var d = new EvdevGamepadDecoder();
            Assert.Equal(new[] { new GamepadInput(expected, true) }, d.Feed(Evdev.EV_ABS, code, value));
            Assert.Equal(new[] { new GamepadInput(expected, false) }, d.Feed(Evdev.EV_ABS, code, 0));
        }

        [Fact]
        public void Stick_EngagesPastDeadzone_AndReleasesWhenCentred()
        {
            var d = new EvdevGamepadDecoder();          // default -32768..32767
            Assert.Empty(d.Feed(Evdev.EV_ABS, Evdev.ABS_Y, -3000));   // inside the deadzone
            Assert.Equal(new[] { new GamepadInput(GamepadAction.Up, true) },
                d.Feed(Evdev.EV_ABS, Evdev.ABS_Y, -30000));
            Assert.Equal(new[] { new GamepadInput(GamepadAction.Up, false) },
                d.Feed(Evdev.EV_ABS, Evdev.ABS_Y, 0));
        }

        [Fact]
        public void Stick_Hysteresis_DoesNotChatterNearTheThreshold()
        {
            var d = new EvdevGamepadDecoder();
            d.Feed(Evdev.EV_ABS, Evdev.ABS_X, 30000);                  // engaged Right
            // Drifting back to just under the engage point must NOT release…
            Assert.Empty(d.Feed(Evdev.EV_ABS, Evdev.ABS_X, 15000));
            // …only falling below the lower release threshold does.
            Assert.Equal(new[] { new GamepadInput(GamepadAction.Right, false) },
                d.Feed(Evdev.EV_ABS, Evdev.ABS_X, 5000));
        }

        [Fact]
        public void Stick_HonoursDeviceReportedRange_ForNon16BitPads()
        {
            // A pad reporting 0..255 centred at 127 (common for older/8-bit axes).
            var d = new EvdevGamepadDecoder(new AxisRange(0, 255), new AxisRange(0, 255));
            Assert.Empty(d.Feed(Evdev.EV_ABS, Evdev.ABS_X, 130));                 // centre
            Assert.Equal(new[] { new GamepadInput(GamepadAction.Right, true) },
                d.Feed(Evdev.EV_ABS, Evdev.ABS_X, 250));                          // full right
        }

        [Fact]
        public void DpadAndStick_AreUnioned_SoOneReleaseDoesNotCancelTheOther()
        {
            var d = new EvdevGamepadDecoder();
            Assert.Equal(new[] { new GamepadInput(GamepadAction.Up, true) },
                d.Feed(Evdev.EV_ABS, Evdev.ABS_HAT0Y, -1));      // D-pad up
            Assert.Empty(d.Feed(Evdev.EV_ABS, Evdev.ABS_Y, -30000)); // stick also up: already pressed
            Assert.Empty(d.Feed(Evdev.EV_ABS, Evdev.ABS_HAT0Y, 0));  // D-pad released, stick still holds
            Assert.Equal(new[] { new GamepadInput(GamepadAction.Up, false) },
                d.Feed(Evdev.EV_ABS, Evdev.ABS_Y, 0));           // now truly released
        }

        [Fact]
        public void DiagonalHold_ReportsBothAxesIndependently()
        {
            var d = new EvdevGamepadDecoder();
            Assert.Equal(new[] { new GamepadInput(GamepadAction.Up, true) }, d.Feed(Evdev.EV_ABS, Evdev.ABS_HAT0Y, -1));
            Assert.Equal(new[] { new GamepadInput(GamepadAction.Right, true) }, d.Feed(Evdev.EV_ABS, Evdev.ABS_HAT0X, 1));
        }

        [Fact]
        public void ShortFrame_IsIgnored_RatherThanThrowing()
            => Assert.Empty(new EvdevGamepadDecoder().Feed(new byte[8]));
        // --- Scroll stick (right stick) ---

        [Fact]
        public void RightStick_ScrollsWithoutMovingFocus()
        {
            // The whole point of the right stick: it must never navigate.
            var d = new EvdevGamepadDecoder();
            Assert.Empty(d.Feed(Frame(Evdev.EV_ABS, Evdev.ABS_RY, 32767)));
            Assert.Empty(d.Feed(Frame(Evdev.EV_ABS, Evdev.ABS_RX, -32768)));

            Assert.True(d.Scroll.Y > 0, "pushing down must scroll down");
            Assert.True(d.Scroll.X < 0, "pushing left must scroll left");
        }

        [Fact]
        public void RightStick_Centred_IsIdle()
        {
            var d = new EvdevGamepadDecoder();
            d.Feed(Frame(Evdev.EV_ABS, Evdev.ABS_RY, 32767));
            Assert.False(d.Scroll.IsIdle);

            d.Feed(Frame(Evdev.EV_ABS, Evdev.ABS_RY, 0));
            Assert.True(d.Scroll.IsIdle);
        }

        [Fact]
        public void RightStick_RestingOffCentre_DoesNotDrift()
        {
            // A worn stick sitting just off centre would otherwise scroll forever.
            var d = new EvdevGamepadDecoder();
            d.Feed(Frame(Evdev.EV_ABS, Evdev.ABS_RY, (int)(32767 * 0.15)));
            Assert.True(d.Scroll.IsIdle);
        }

        [Fact]
        public void RightStick_HonoursTheDeviceAxisRange()
        {
            // Pads that report 8-bit axes must reach full speed too.
            var d = new EvdevGamepadDecoder(rxRange: new AxisRange(0, 255), ryRange: new AxisRange(0, 255));
            d.Feed(Frame(Evdev.EV_ABS, Evdev.ABS_RY, 255));
            Assert.Equal(1.0, d.Scroll.Y, 3);
        }

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(0.2, 0.0)]      // exactly on the deadzone edge: still nothing
        [InlineData(-0.1, 0.0)]
        [InlineData(1.0, 1.0)]
        [InlineData(-1.0, -1.0)]
        public void ScrollCurve_IsFlatInsideTheDeadzoneAndFullAtTheEdge(double input, double expected)
            => Assert.Equal(expected, EvdevGamepadDecoder.ScrollAxis(input), 6);

        [Fact]
        public void ScrollCurve_RampsUpGently()
        {
            // Squared response: half a push should be well under half speed, so small
            // corrections stay controllable.
            var half = EvdevGamepadDecoder.ScrollAxis(0.6);
            Assert.InRange(half, 0.01, 0.45);
            Assert.True(EvdevGamepadDecoder.ScrollAxis(0.9) > half, "further must be faster");
        }

        [Fact]
        public void LeftStick_DoesNotScroll()
        {
            var d = new EvdevGamepadDecoder();
            d.Feed(Frame(Evdev.EV_ABS, Evdev.ABS_Y, 32767));
            Assert.True(d.Scroll.IsIdle);
        }

    }

    public class DirectionRepeaterTests
    {
        private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void DoesNotRepeatBeforeTheInitialDelay()
        {
            var r = new DirectionRepeater(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100));
            r.Press(GamepadAction.Down, T0);
            Assert.Empty(r.Tick(T0.AddMilliseconds(399)));
        }

        [Fact]
        public void RepeatsOnIntervalAfterTheInitialDelay()
        {
            var r = new DirectionRepeater(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100));
            r.Press(GamepadAction.Down, T0);
            Assert.Equal(new[] { GamepadAction.Down }, r.Tick(T0.AddMilliseconds(400)));
            Assert.Empty(r.Tick(T0.AddMilliseconds(450)));                                   // too soon
            Assert.Equal(new[] { GamepadAction.Down }, r.Tick(T0.AddMilliseconds(500)));
        }

        [Fact]
        public void ReleaseStopsRepeating()
        {
            var r = new DirectionRepeater(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100));
            r.Press(GamepadAction.Down, T0);
            r.Release(GamepadAction.Down);
            Assert.Empty(r.Tick(T0.AddMilliseconds(900)));
        }

        [Fact]
        public void ActionButtons_NeverRepeat_SoAConfirmIsOnlyEverOnePress()
        {
            var r = new DirectionRepeater(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100));
            r.Press(GamepadAction.Accept, T0);
            Assert.Empty(r.Tick(T0.AddMilliseconds(5000)));
        }
    }

    public class GamepadDiscoveryTests
    {
        [Fact]
        public void FindsOnlyJoystickEventNodes()
        {
            var root = Path.Combine(Path.GetTempPath(), "osm_input_" + Guid.NewGuid().ToString("N"));
            var byId = Path.Combine(root, "by-id");
            Directory.CreateDirectory(byId);
            // udev names controllers "*-event-joystick"; keyboards/mice must be ignored.
            File.WriteAllText(Path.Combine(byId, "usb-Microsoft_X-Box_360_pad-event-joystick"), "");
            File.WriteAllText(Path.Combine(byId, "usb-Some_Keyboard-event-kbd"), "");
            File.WriteAllText(Path.Combine(byId, "usb-Some_Mouse-event-mouse"), "");
            File.WriteAllText(Path.Combine(byId, "usb-Microsoft_X-Box_360_pad-joystick"), ""); // legacy js node
            try
            {
                var found = EvdevGamepadSource.DiscoverDevicePaths(root).ToList();
                Assert.Single(found);
                Assert.EndsWith("usb-Microsoft_X-Box_360_pad-event-joystick", found[0]);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void MissingInputRoot_YieldsNothing()
            => Assert.Empty(EvdevGamepadSource.DiscoverDevicePaths("/definitely/not/here"));

        [Fact]
        public void CandidateScan_IncludesRawEventNodes_NotJustUdevSymlinks()
        {
            // Virtual pads (e.g. the one Steam exposes in Gaming Mode) often have no
            // by-id/by-path symlink, so raw event* nodes must be probed too.
            var root = Path.Combine(Path.GetTempPath(), "osm_input_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "by-id"));
            File.WriteAllText(Path.Combine(root, "event0"), "");
            File.WriteAllText(Path.Combine(root, "event12"), "");
            File.WriteAllText(Path.Combine(root, "mice"), "");   // not an event node
            File.WriteAllText(Path.Combine(root, "by-id", "usb-Pad-event-joystick"), "");
            try
            {
                var candidates = EvdevGamepadSource.DiscoverCandidatePaths(root).Select(Path.GetFileName).ToList();
                Assert.Contains("event0", candidates);
                Assert.Contains("event12", candidates);
                Assert.Contains("usb-Pad-event-joystick", candidates);
                Assert.DoesNotContain("mice", candidates);
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void CandidateScan_MissingRoot_IsEmpty()
            => Assert.Empty(EvdevGamepadSource.DiscoverCandidatePaths("/definitely/not/here"));

        [Theory]
        [InlineData("usb-Microsoft_X-Box_360_pad-event-joystick", "Microsoft X-Box 360 pad")]
        [InlineData("usb-Valve_Software_Steam_Controller-event-joystick", "Valve Software Steam Controller")]
        [InlineData("bluetooth-8BitDo_Pro_2-event-joystick", "8BitDo Pro 2")]
        public void DerivesReadableDeviceNames(string file, string expected)
            => Assert.Equal(expected, EvdevGamepadSource.FriendlyName("/dev/input/by-id/" + file));
    }
}
