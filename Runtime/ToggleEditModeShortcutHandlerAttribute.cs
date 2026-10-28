using System;

/// <summary>
/// Marks a static <c>bool Method()</c> as a fallback handler for the Editools "Toggle Edit Mode"
/// shortcut (Alt+~). The shortcut toggles Grey Primitive Edit Mode when a Grey Primitive is selected;
/// otherwise it calls each marked method until one returns true (the press was handled).
/// Lets other assemblies share the key without Editools referencing them.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ToggleEditModeShortcutHandlerAttribute : Attribute { }
