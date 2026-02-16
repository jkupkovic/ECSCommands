using System;
using Unity.Entities;
using MoleHill.EcsCommands.Enums;

namespace MoleHill.EcsCommands
{

    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class EcsEntityPickerAttribute : Attribute
    {
        // Required (ALL)
        public Type[] All { get; }
        public EcsComponentAccess AllAccess { get; }

        // Optional: Any-of (OR)
        public Type[] Any { get; }
        public EcsComponentAccess AnyAccess { get; }

        // Optional: None-of (NOT)
        public Type[] None { get; }

        public EcsEntityPickerAttribute(
            Type[] all,
            EcsComponentAccess allAccess = EcsComponentAccess.ReadOnly,
            Type[]? any = null,
            EcsComponentAccess anyAccess = EcsComponentAccess.ReadOnly,
            Type[]? none = null)
        {
            All = all ?? Array.Empty<Type>();
            Any = any ?? Array.Empty<Type>();
            None = none ?? Array.Empty<Type>();
            AllAccess = allAccess;
            AnyAccess = anyAccess;
        }
    }
}