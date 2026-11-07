using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VolcanoBlue.EventSourcing.EventStore.Serialization
{
    /// <summary>
    /// [INFRASTRUCTURE - SERIALIZATION] Universal JSON converter for types with private constructors.
    /// Architectural Role: Enables deserialization of domain value objects by invoking private constructors via reflection.
    /// Preserves domain model purity by keeping serialization concerns in infrastructure layer.
    /// </summary>
    internal sealed class PrivateConstructorConverter : JsonConverterFactory
    {
        private static readonly ConcurrentDictionary<Type, bool> CanConvertCache = [];

        public override bool CanConvert(Type typeToConvert)
        {
            return CanConvertCache.GetOrAdd(typeToConvert, static type =>
            {
                var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                var hasPrivateConstructor = constructors.Any(c => !c.IsPublic);
                if (!hasPrivateConstructor)
                    return false;

                var publicConstructors = constructors.Where(c => c.IsPublic);
                var hasOnlyParameterlessPublicConstructor = !publicConstructors.Any() ||
                                                            publicConstructors.All(c => c.GetParameters().Length == 0);

                return hasOnlyParameterlessPublicConstructor;
            });
        }

        public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var converterType = typeof(PrivateConstructorConverterInner<>).MakeGenericType(typeToConvert);
            return (JsonConverter?)Activator.CreateInstance(converterType);
        }

        private sealed class PrivateConstructorConverterInner<T> : JsonConverter<T>
        {
            private static readonly ConstructorInfo? Constructor;
            private static readonly ParameterInfo[]? Parameters;
            private static readonly PropertyNameMapping[]? PropertyMappings;
            private static readonly Func<object?[], T>? CompiledConstructor;
            private static readonly int ParameterCount;

            static PrivateConstructorConverterInner()
            {
                // Cache constructor and parameter metadata at type initialization
                Constructor = typeof(T)
                    .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .OrderByDescending(c => c.GetParameters().Length)
                    .FirstOrDefault();

                if (Constructor is not null)
                {
                    Parameters = Constructor.GetParameters();
                    ParameterCount = Parameters.Length;
                    PropertyMappings = new PropertyNameMapping[ParameterCount];

                    for (int i = 0; i < ParameterCount; i++)
                    {
                        var paramName = Parameters[i].Name!;
                        PropertyMappings[i] = new PropertyNameMapping(
                            paramName,
                            paramName.Length > 0 ? char.ToUpperInvariant(paramName[0]) + paramName[1..] : paramName
                        );
                    }

                    // Compile expression tree for fast constructor invocation
                    CompiledConstructor = BuildConstructorDelegate();
                }
            }

            private static Func<object?[], T> BuildConstructorDelegate()
            {
                if (Constructor is null || Parameters is null)
                    throw new InvalidOperationException("Constructor not found");

                // Handle parameterless constructor
                if (Parameters.Length == 0)
                {
                    var constructorCall = Expression.New(Constructor);
                    var lambda = Expression.Lambda<Func<object?[], T>>(
                        constructorCall,
                        Expression.Parameter(typeof(object?[]), "args")
                    );
                    return lambda.Compile();
                }

                // Create parameter: object?[] args
                var argsParam = Expression.Parameter(typeof(object?[]), "args");

                // Create expressions: (TParam)args[i] for each parameter
                var parameterExpressions = new Expression[Parameters.Length];
                for (int i = 0; i < Parameters.Length; i++)
                {
                    var paramType = Parameters[i].ParameterType;
                    var arrayAccess = Expression.ArrayIndex(argsParam, Expression.Constant(i));

                    // Handle nullable value types and reference types
                    Expression convertExpression;
                    if (paramType.IsValueType && Nullable.GetUnderlyingType(paramType) == null)
                    {
                        // Non-nullable value type: handle null by providing default value
                        var nullCheck = Expression.Equal(arrayAccess, Expression.Constant(null, typeof(object)));
                        var defaultValue = Expression.Default(paramType);
                        var convertedValue = Expression.Convert(arrayAccess, paramType);
                        convertExpression = Expression.Condition(nullCheck, defaultValue, convertedValue);
                    }
                    else
                    {
                        // Nullable value types or reference types: direct conversion
                        convertExpression = Expression.Convert(arrayAccess, paramType);
                    }

                    parameterExpressions[i] = convertExpression;
                }

                // Create: new T((TParam1)args[0], (TParam2)args[1], ...)
                var constructorCall2 = Expression.New(Constructor, parameterExpressions);

                // Compile: object?[] => new T(...)
                var lambda2 = Expression.Lambda<Func<object?[], T>>(constructorCall2, argsParam);
                return lambda2.Compile();
            }

            public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Null)
                    return default;

                if (Constructor is null || Parameters is null || PropertyMappings is null || CompiledConstructor is null)
                    return default;

                // Handle parameterless constructor
                if (ParameterCount == 0)
                {
                    return CompiledConstructor([]);
                }

                using var doc = JsonDocument.ParseValue(ref reader);
                var root = doc.RootElement;

                // Rent array from pool for parameter values - handle any size
                var parameterValues = ParameterCount <= 256
                    ? ArrayPool<object?>.Shared.Rent(ParameterCount)
                    : new object?[ParameterCount]; // Fallback for extremely large parameter lists

                try
                {
                    // Match JSON properties to constructor parameters
                    for (int i = 0; i < ParameterCount; i++)
                    {
                        var param = Parameters[i];
                        var mapping = PropertyMappings[i];

                        // Try to find matching property in JSON (case-insensitive)
                        if (root.TryGetProperty(mapping.LowerCase, out var jsonValue) ||
                            root.TryGetProperty(mapping.UpperCase, out jsonValue))
                        {
                            parameterValues[i] = JsonSerializer.Deserialize(jsonValue.GetRawText(), param.ParameterType, options);
                        }
                        else
                        {
                            // Use default value if property not found
                            parameterValues[i] = param.HasDefaultValue
                                ? param.DefaultValue
                                : GetDefaultValue(param.ParameterType);
                        }
                    }

                    // Invoke compiled constructor delegate
                    return CompiledConstructor(parameterValues);
                }
                finally
                {
                    // Return array to pool only if it was rented
                    if (ParameterCount <= 256)
                    {
                        ArrayPool<object?>.Shared.Return(parameterValues, clearArray: true);
                    }
                }
            }

            private static object? GetDefaultValue(Type type)
            {
                // Handle nullable value types
                var underlyingType = Nullable.GetUnderlyingType(type);
                if (underlyingType != null)
                    return null;

                // Handle value types
                if (type.IsValueType)
                    return Activator.CreateInstance(type);

                // Reference types default to null
                return null;
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                JsonSerializer.Serialize(writer, value, value!.GetType(), options);
            }

            private readonly record struct PropertyNameMapping(string LowerCase, string UpperCase);
        }
    }
}