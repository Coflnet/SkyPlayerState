using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Services;

namespace Coflnet.Sky.PlayerState.Tests;

public class MockedUpdateArgs : UpdateArgs
    {
        private Dictionary<Type, object> services = new();
        public override T GetService<T>()
        {
            if (services.ContainsKey(typeof(T)))
                return (T)services[typeof(T)];
            throw new Exception($"Service {typeof(T)} not found");
        }

        public void AddService<T>(T service)
        {
            services[typeof(T)] = service;
        }
    }