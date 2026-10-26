// Adapted from the ExtendedUISystemBase / ValueBindingHelper utilities shared by
// FindIt-CSII and CS2-Platter (MIT License; T. D. W., yenyang, Luca Rager).
// Changes: Copyright (c) 2026 dvovo. https://mods.paradoxplaza.com/authors/dvovo
// Licensed under the MIT License. See LICENSE in the repository root.

namespace CustomZones.Utilities
{
    using Colossal.UI.Binding;
    using Game.UI;
    using System;

    /// <summary>
    /// UI system base with helpers that create value bindings and triggers for
    /// any type (read and written through GenericUIReader / GenericUIWriter).
    /// </summary>
    public abstract partial class ExtendedUISystemBase : UISystemBase
    {
        public ValueBindingHelper<T> CreateBinding<T>(string key, T initialValue)
        {
            var helper = new ValueBindingHelper<T>(
                new ValueBinding<T>(Mod.Id, key, initialValue,
                    new GenericUIWriter<T>()));

            AddBinding(helper.Binding);
            return helper;
        }

        public ValueBindingHelper<T> CreateBinding<T>(string key, string setterKey, T initialValue, Action<T> updateCallBack = null)
        {
            var helper = new ValueBindingHelper<T>(
                new ValueBinding<T>(Mod.Id, key, initialValue,
                    new GenericUIWriter<T>()), updateCallBack);

            AddBinding(helper.Binding);
            AddBinding(new TriggerBinding<T>(Mod.Id, setterKey,
                helper.UpdateCallback, new GenericUIReader<T>()));

            return helper;
        }

        public TriggerBinding CreateTrigger(string key, Action action)
        {
            var binding = new TriggerBinding(Mod.Id, key, action);
            AddBinding(binding);
            return binding;
        }

        public TriggerBinding<T1> CreateTrigger<T1>(string key, Action<T1> action)
        {
            var binding = new TriggerBinding<T1>(Mod.Id, key, action,
                new GenericUIReader<T1>());
            AddBinding(binding);
            return binding;
        }

        public TriggerBinding<T1, T2> CreateTrigger<T1, T2>(string key, Action<T1, T2> action)
        {
            var binding = new TriggerBinding<T1, T2>(Mod.Id, key, action,
                new GenericUIReader<T1>(), new GenericUIReader<T2>());
            AddBinding(binding);
            return binding;
        }

        public TriggerBinding<T1, T2, T3> CreateTrigger<T1, T2, T3>(string key, Action<T1, T2, T3> action)
        {
            var binding = new TriggerBinding<T1, T2, T3>(Mod.Id, key, action,
                new GenericUIReader<T1>(), new GenericUIReader<T2>(), new GenericUIReader<T3>());
            AddBinding(binding);
            return binding;
        }
    }

    /// <summary>
    /// A value binding with an optional callback for values set from the UI.
    /// </summary>
    public class ValueBindingHelper<T>
    {
        private readonly Action<T> _updateCallBack;

        public ValueBinding<T> Binding { get; }

        public T Value
        {
            get => Binding.value;
            set => Binding.Update(value);
        }

        public ValueBindingHelper(ValueBinding<T> binding, Action<T> updateCallBack = null)
        {
            Binding = binding;
            _updateCallBack = updateCallBack;
        }

        public void UpdateCallback(T value)
        {
            Binding.Update(value);
            _updateCallBack?.Invoke(value);
        }

        public static implicit operator T(ValueBindingHelper<T> helper) => helper.Binding.value;
    }
}
