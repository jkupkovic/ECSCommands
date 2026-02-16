using System;

namespace MoleHill.EcsCommands
{
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class EcsFromEntityRefAttribute : Attribute
    {
        public string Reference { get; }

        // Optional extra requirements beyond "component type of the parameter"
        public Type[] ExtraAll { get; }

        public EcsFromEntityRefAttribute(string reference, params Type[] extraAll)
        {
            Reference = reference;
            ExtraAll = extraAll ?? Array.Empty<Type>();
        }
    }
}