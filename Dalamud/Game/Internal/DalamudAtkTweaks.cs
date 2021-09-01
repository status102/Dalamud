using System;
using System.Runtime.InteropServices;
using System.Text;

using CheapLoc;
using Dalamud.Configuration.Internal;
using Dalamud.Hooking;
using Dalamud.Interface.Internal;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Serilog;

using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace Dalamud.Game.Internal
{
    /// <summary>
    /// This class implements in-game Dalamud options in the in-game System menu.
    /// </summary>
    internal sealed unsafe partial class DalamudAtkTweaks
    {
        private readonly AtkValueChangeType atkValueChangeType;
        private readonly AtkValueSetString atkValueSetString;
        private readonly Hook<AgentHudOpenSystemMenuPrototype> hookAgentHudOpenSystemMenu;

        // TODO: Make this into events in Framework.Gui
        private readonly Hook<UiModuleRequestMainCommand> hookUiModuleRequestMainCommand;

        private readonly Hook<AtkUnitBaseReceiveGlobalEvent> hookAtkUnitBaseReceiveGlobalEvent;

        /// <summary>
        /// Initializes a new instance of the <see cref="DalamudAtkTweaks"/> class.
        /// </summary>
        public DalamudAtkTweaks()
        {
            var sigScanner = Service<SigScanner>.Get();

            var openSystemMenuAddress = sigScanner.ScanText("E8 ?? ?? ?? ?? 32 C0 4C 8B AC 24 ?? ?? ?? ?? 48 8B 8D ?? ?? ?? ??");

            this.hookAgentHudOpenSystemMenu = new Hook<AgentHudOpenSystemMenuPrototype>(openSystemMenuAddress, this.AgentHudOpenSystemMenuDetour);

            var atkValueChangeTypeAddress = sigScanner.ScanText("E8 ?? ?? ?? ?? 45 84 F6 48 8D 4C 24 ??");
            this.atkValueChangeType = Marshal.GetDelegateForFunctionPointer<AtkValueChangeType>(atkValueChangeTypeAddress);

            var atkValueSetStringAddress = sigScanner.ScanText("E8 ?? ?? ?? ?? 41 03 ED");
            this.atkValueSetString = Marshal.GetDelegateForFunctionPointer<AtkValueSetString>(atkValueSetStringAddress);

            var uiModuleRequestMainCommandAddress = sigScanner.ScanText("40 53 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B 01 8B DA 48 8B F1 FF 90 ?? ?? ?? ??");
            this.hookUiModuleRequestMainCommand = new Hook<UiModuleRequestMainCommand>(uiModuleRequestMainCommandAddress, this.UiModuleRequestMainCommandDetour);

            var atkUnitBaseReceiveGlobalEventAddress =
                sigScanner.ScanText("48 89 5C 24 ?? 48 89 7C 24 ?? 55 41 56 41 57 48 8B EC 48 83 EC 50 44 0F B7 F2 ");
            this.hookAtkUnitBaseReceiveGlobalEvent =
                new Hook<AtkUnitBaseReceiveGlobalEvent>(atkUnitBaseReceiveGlobalEventAddress, this.AtkUnitBaseReceiveGlobalEventDetour);
        }

        private delegate void AgentHudOpenSystemMenuPrototype(void* thisPtr, AtkValue* atkValueArgs, uint menuSize);

        private delegate void AtkValueChangeType(AtkValue* thisPtr, ValueType type);

        private delegate void AtkValueSetString(AtkValue* thisPtr, byte* bytes);

        private delegate void UiModuleRequestMainCommand(void* thisPtr, int commandId);

        private delegate IntPtr AtkUnitBaseReceiveGlobalEvent(AtkUnitBase* thisPtr, ushort cmd, uint a3, IntPtr a4, uint* a5);

        /// <summary>
        /// Enables the <see cref="DalamudAtkTweaks"/>.
        /// </summary>
        public void Enable()
        {
            this.hookAgentHudOpenSystemMenu.Enable();
            this.hookUiModuleRequestMainCommand.Enable();
            this.hookAtkUnitBaseReceiveGlobalEvent.Enable();
        }

        private IntPtr AtkUnitBaseReceiveGlobalEventDetour(AtkUnitBase* thisPtr, ushort cmd, uint a3, IntPtr a4, uint* a5)
        {
            var di = Service<DalamudInterface>.Get();

            // "Close Addon"
            if (cmd == 12 && di.WindowSystem.HasAnyFocus)
                return IntPtr.Zero;

            return this.hookAtkUnitBaseReceiveGlobalEvent.Original(thisPtr, cmd, a3, a4, a5);
        }

        private void AgentHudOpenSystemMenuDetour(void* thisPtr, AtkValue* atkValueArgs, uint menuSize)
        {
            var configuration = Service<DalamudConfiguration>.Get();

            if (!configuration.DoButtonsSystemMenu)
            {
                this.hookAgentHudOpenSystemMenu.Original(thisPtr, atkValueArgs, menuSize);
                return;
            }

            // the max size (hardcoded) is 0xE/15, but the system menu currently uses 0xC/12
            // this is a just in case that doesnt really matter
            // see if we can add 2 entries
            if (menuSize >= 0xD)
            {
                this.hookAgentHudOpenSystemMenu.Original(thisPtr, atkValueArgs, menuSize);
                return;
            }

            // atkValueArgs is actually an array of AtkValues used as args. all their UI code works like this.
            // in this case, menu size is stored in atkValueArgs[4], and the next 15 slots are the MainCommand
            // the 15 slots after that, if they exist, are the entry names, but they are otherwise pulled from MainCommand EXD
            // reference the original function for more details :)

            // step 1) move all the current menu items down so we can put Dalamud at the top like it deserves
            this.atkValueChangeType(&atkValueArgs[menuSize + 5], ValueType.Int); // currently this value has no type, set it to int
            this.atkValueChangeType(&atkValueArgs[menuSize + 5 + 1], ValueType.Int);

            for (var i = menuSize + 2; i > 1; i--)
            {
                var curEntry = &atkValueArgs[i + 5 - 2];
                var nextEntry = &atkValueArgs[i + 5];

                nextEntry->Int = curEntry->Int;
            }

            // step 2) set our new entries to dummy commands
            var firstEntry = &atkValueArgs[5];
            firstEntry->Int = 69420;
            var secondEntry = &atkValueArgs[6];
            secondEntry->Int = 69421;

            // step 3) create strings for them
            // since the game first checks for strings in the AtkValue argument before pulling them from the exd, if we create strings we dont have to worry
            // about hooking the exd reader, thank god
            var firstStringEntry = &atkValueArgs[5 + 15];
            this.atkValueChangeType(firstStringEntry, ValueType.String);
            var secondStringEntry = &atkValueArgs[6 + 15];
            this.atkValueChangeType(secondStringEntry, ValueType.String);

            var strPlugins = Encoding.UTF8.GetBytes(Loc.Localize("SystemMenuPlugins", "Dalamud Plugins"));
            var strSettings = Encoding.UTF8.GetBytes(Loc.Localize("SystemMenuSettings", "Dalamud Settings"));

            // do this the most terrible way possible since im lazy
            var bytes = stackalloc byte[strPlugins.Length + 1];
            Marshal.Copy(strPlugins, 0, new IntPtr(bytes), strPlugins.Length);
            bytes[strPlugins.Length] = 0x0;

            this.atkValueSetString(firstStringEntry, bytes); // this allocs the string properly using the game's allocators and copies it, so we dont have to worry about memory fuckups

            var bytes2 = stackalloc byte[strSettings.Length + 1];
            Marshal.Copy(strSettings, 0, new IntPtr(bytes2), strSettings.Length);
            bytes2[strSettings.Length] = 0x0;

            this.atkValueSetString(secondStringEntry, bytes2);

            // open menu with new size
            var sizeEntry = &atkValueArgs[4];
            sizeEntry->UInt = menuSize + 2;

            this.hookAgentHudOpenSystemMenu.Original(thisPtr, atkValueArgs, menuSize + 2);
        }

        private void UiModuleRequestMainCommandDetour(void* thisPtr, int commandId)
        {
            var dalamudInterface = Service<DalamudInterface>.Get();

            switch (commandId)
            {
                case 69420:
                    dalamudInterface.TogglePluginInstallerWindow();
                    break;
                case 69421:
                    dalamudInterface.ToggleSettingsWindow();
                    break;
                default:
                    this.hookUiModuleRequestMainCommand.Original(thisPtr, commandId);
                    break;
            }
        }
    }

    /// <summary>
    /// Implements IDisposable.
    /// </summary>
    internal sealed partial class DalamudAtkTweaks : IDisposable
    {
        private bool disposed = false;

        /// <summary>
        /// Finalizes an instance of the <see cref="DalamudAtkTweaks"/> class.
        /// </summary>
        ~DalamudAtkTweaks() => this.Dispose(false);

        /// <summary>
        /// Dispose of managed and unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Dispose of managed and unmanaged resources.
        /// </summary>
        private void Dispose(bool disposing)
        {
            if (this.disposed)
                return;

            if (disposing)
            {
                this.hookAgentHudOpenSystemMenu.Dispose();
                this.hookUiModuleRequestMainCommand.Dispose();
                this.hookAtkUnitBaseReceiveGlobalEvent.Dispose();
            }

            this.disposed = true;
        }
    }
}
