﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;

namespace Oxide.Game.Rust.Libraries
{
    /// <summary>
    /// A library containing functions for adding console and chat commands
    /// </summary>
    public class Command : Library
    {
        private static string ReturnEmptyString() => string.Empty;
        private static void DoNothing(string str) { }

        private struct PluginCallback
        {
            public readonly Plugin Plugin;
            public readonly string Name;
            public Func<ConsoleSystem.Arg, bool> Callback;

            public PluginCallback(Plugin plugin, string name)
            {
                Plugin = plugin;
                Name = name;
                Callback = null;
            }

            public PluginCallback(Plugin plugin, Func<ConsoleSystem.Arg, bool> callback)
            {
                Plugin = plugin;
                Callback = callback;
                Name = null;
            }
        }

        private class ConsoleCommand
        {
            public readonly string Name;
            public readonly List<PluginCallback> PluginCallbacks = new List<PluginCallback>();
            public readonly ConsoleSystem.Command RustCommand;
            public Action<ConsoleSystem.Arg> OriginalCallback;

            public ConsoleCommand(string name)
            {
                Name = name;
                var splitName = Name.Split('.');
                RustCommand = new ConsoleSystem.Command
                {
                    name = splitName[1],
                    parent = splitName[0],
                    namefull = name,
                    isCommand = true,
                    isUser = true,
                    isAdmin = true,
                    GetString = ReturnEmptyString,
                    SetString = DoNothing,
                    Call = HandleCommand
                };
            }

            public void AddCallback(Plugin plugin, string name)
            {
                PluginCallbacks.Add(new PluginCallback(plugin, name));
            }

            public void AddCallback(Plugin plugin, Func<ConsoleSystem.Arg, bool> callback)
            {
                PluginCallbacks.Add(new PluginCallback(plugin, callback));
            }

            public void HandleCommand(ConsoleSystem.Arg arg)
            {
                foreach (var pluginCallback in PluginCallbacks)
                {
                    pluginCallback.Plugin?.TrackStart();
                    var result = pluginCallback.Callback(arg);
                    pluginCallback.Plugin?.TrackEnd();
                    if (result) return;
                }

                OriginalCallback?.Invoke(arg);
            }
        }

        private class ChatCommand
        {
            public readonly string Name;
            public readonly Plugin Plugin;
            private readonly Action<BasePlayer, string, string[]> _callback;

            public ChatCommand(string name, Plugin plugin, Action<BasePlayer, string, string[]> callback)
            {
                Name = name;
                Plugin = plugin;
                _callback = callback;
            }

            public void HandleCommand(BasePlayer sender, string name, string[] args)
            {
                Plugin?.TrackStart();
                _callback?.Invoke(sender, name, args);
                Plugin?.TrackEnd();
            }
        }

        // All console commands that plugins have registered
        private readonly Dictionary<string, ConsoleCommand> consoleCommands;

        // All chat commands that plugins have registered
        private readonly Dictionary<string, ChatCommand> chatCommands;

        // A reference to Rust's internal command dictionary
        private IDictionary<string, ConsoleSystem.Command> rustcommands;

        // A reference to the plugin removed callbacks
        private readonly Dictionary<Plugin, Event.Callback<Plugin, PluginManager>> pluginRemovedFromManager;

        /// <summary>
        /// Initializes a new instance of the Command class
        /// </summary>
        public Command()
        {
            consoleCommands = new Dictionary<string, ConsoleCommand>();
            chatCommands = new Dictionary<string, ChatCommand>();
            pluginRemovedFromManager = new Dictionary<Plugin, Event.Callback<Plugin, PluginManager>>();
        }

        /// <summary>
        /// Adds a chat command
        /// </summary>
        /// <param name="name"></param>
        /// <param name="plugin"></param>
        /// <param name="callback"></param>
        [LibraryFunction("AddChatCommand")]
        public void AddChatCommand(string name, Plugin plugin, string callback)
        {
            AddChatCommand(name, plugin, (player, command, args) => plugin.CallHook(callback, player, command, args));
        }

        /// <summary>
        /// Adds a chat command
        /// </summary>
        /// <param name="name"></param>
        /// <param name="plugin"></param>
        /// <param name="callback"></param>
        public void AddChatCommand(string name, Plugin plugin, Action<BasePlayer, string, string[]> callback)
        {
            var commandName = name.ToLowerInvariant();

            ChatCommand cmd;
            if (chatCommands.TryGetValue(commandName, out cmd))
            {
                var previousPluginName = cmd.Plugin?.Name ?? "an unknown plugin";
                var newPluginName = plugin?.Name ?? "An unknown plugin";
                var message = $"{newPluginName} has replaced the '{commandName}' chat command previously registered by {previousPluginName}";
                Interface.Oxide.LogWarning(message);
            }

            cmd = new ChatCommand(commandName, plugin, callback);

            // Add the new command to collections
            chatCommands[commandName] = cmd;

            // Hook the unload event
            if (plugin != null && !pluginRemovedFromManager.ContainsKey(plugin))
                pluginRemovedFromManager[plugin] = plugin.OnRemovedFromManager.Add(plugin_OnRemovedFromManager);
        }

        /// <summary>
        /// Adds a console command
        /// </summary>
        /// <param name="plugin"></param>
        /// <param name="callback"></param>
        [LibraryFunction("AddConsoleCommand")]
        public void AddConsoleCommand(string name, Plugin plugin, string callback)
        {
            AddConsoleCommand(name, plugin, arg => plugin.CallHook(callback, arg) != null);
        }

        /// <summary>
        /// Adds a console command with a delegate callback
        /// </summary>
        /// <param name="name"></param>
        /// <param name="plugin"></param>
        /// <param name="callback"></param>
        public void AddConsoleCommand(string name, Plugin plugin, Func<ConsoleSystem.Arg, bool> callback)
        {
            // Hack us the dictionary
            if (rustcommands == null) rustcommands = typeof(ConsoleSystem.Index).GetField("dictionary", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IDictionary<string, ConsoleSystem.Command>;

            // Hook the unload event
            if (plugin != null && !pluginRemovedFromManager.ContainsKey(plugin))
                pluginRemovedFromManager[plugin] = plugin.OnRemovedFromManager.Add(plugin_OnRemovedFromManager);

            var fullName = name.Trim();

            ConsoleCommand cmd;
            if (consoleCommands.TryGetValue(fullName, out cmd))
            {
                // Another plugin registered this command
                if (cmd.OriginalCallback != null)
                {
                    // This is a vanilla rust command which has already been pre-hooked by another plugin
                    cmd.AddCallback(plugin, callback);
                    return;
                }

                // This is a custom command which was already registered by another plugin
                var previousPluginName = cmd.PluginCallbacks[0].Plugin?.Name ?? "an unknown plugin";
                var newPluginName = plugin?.Name ?? "An unknown plugin";
                var message = $"{newPluginName} has replaced the '{name}' console command previously registered by {previousPluginName}";
                Interface.Oxide.LogWarning(message);
                rustcommands.Remove(fullName);
                ConsoleSystem.Index.GetAll().Remove(cmd.RustCommand);
            }

            // The command either does not already exist or is replacing a previously registered command
            cmd = new ConsoleCommand(fullName);
            cmd.AddCallback(plugin, callback);

            ConsoleSystem.Command rustCommand;
            if (rustcommands.TryGetValue(fullName, out rustCommand))
            {
                // This is a vanilla rust command which has not yet been hooked by a plugin
                if (rustCommand.isVariable)
                {
                    var newPluginName = plugin?.Name ?? "An unknown plugin";
                    Interface.Oxide.LogError($"{newPluginName} tried to register the {name} console variable as a command!");
                    return;
                }
                cmd.OriginalCallback = cmd.RustCommand.Call;
                cmd.RustCommand.Call = cmd.HandleCommand;
            }
            else
            {
                // This is a custom command which needs to be created
                rustcommands[cmd.RustCommand.namefull] = cmd.RustCommand;
                ConsoleSystem.Index.GetAll().Add(cmd.RustCommand);
            }

            consoleCommands[fullName] = cmd;
        }

        /// <summary>
        /// Handles the specified chat command
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="name"></param>
        /// <param name="args"></param>
        internal bool HandleChatCommand(BasePlayer sender, string name, string[] args)
        {
            ChatCommand cmd;
            if (!chatCommands.TryGetValue(name.ToLowerInvariant(), out cmd)) return false;
            cmd.HandleCommand(sender, name, args);
            return true;
        }

        /// <summary>
        /// Called when a plugin has been removed from manager
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="manager"></param>
        private void plugin_OnRemovedFromManager(Plugin sender, PluginManager manager)
        {
            // Find all console commands which were registered by the plugin
            var commands = consoleCommands.Values.Where(c => c.PluginCallbacks.Any(cb => cb.Plugin == sender)).ToArray();
            foreach (var cmd in commands)
            {
                cmd.PluginCallbacks.RemoveAll(cb => cb.Plugin == sender);
                if (cmd.PluginCallbacks.Count > 0) continue;

                // This command is no longer registered by any plugins
                consoleCommands.Remove(cmd.Name);

                if (cmd.OriginalCallback == null)
                {
                    // This is a custom command, remove it completely
                    rustcommands.Remove(cmd.RustCommand.namefull);
                    ConsoleSystem.Index.GetAll().Remove(cmd.RustCommand);
                }
                else
                {
                    // This is a vanilla rust command, restore the original callback
                    cmd.RustCommand.Call = cmd.OriginalCallback;
                }
            }

            // Remove all chat commands which were registered by the plugin
            foreach (var cmd in chatCommands.Values.Where(c => c.Plugin == sender).ToArray())
                chatCommands.Remove(cmd.Name);

            // Unhook the event
            Event.Callback<Plugin, PluginManager> event_callback;
            if (pluginRemovedFromManager.TryGetValue(sender, out event_callback))
            {
                event_callback.Remove();
                pluginRemovedFromManager.Remove(sender);
            }
        }
    }
}
