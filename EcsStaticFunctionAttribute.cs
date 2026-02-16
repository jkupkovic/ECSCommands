using System;
using UnityEngine;
namespace MoleHill.EcsCommands {
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class EcsStaticFunctionAttribute : Attribute
    {
        public readonly string? DisplayName;
        public readonly string? Category;
        public readonly bool ShowInWindow;

        public EcsStaticFunctionAttribute(string? displayName = null, string? category = null, bool showInWindow = true)
        {
            DisplayName = displayName;
            Category = category;
            ShowInWindow = showInWindow;
        }
    }
}
