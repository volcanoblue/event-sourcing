using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VolcanoBlue.EventSourcing.Serialization
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
            private static readonly Func<object?[], T>? CompiledConstructor;
            private static readonly int ParameterCount;
            private static readonly object?[]? Defaults;
            private static readonly Dictionary<string, int>? IndexByName;

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
                    Defaults = new object?[ParameterCount];
                    IndexByName = new Dictionary<string, int>(ParameterCount, StringComparer.OrdinalIgnoreCase);

                    for (int i = 0; i < ParameterCount; i++)
                    {
                        var paramName = Parameters[i].Name!;
                        Defaults[i] = Parameters[i].HasDefaultValue
                            ? Parameters[i].DefaultValue
                            : GetDefaultValue(Parameters[i].ParameterType);
                        IndexByName.TryAdd(paramName, i);
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

                if (Constructor is null || Parameters is null || CompiledConstructor is null)
                    return default;

                // Handle parameterless constructor
                if (ParameterCount == 0)
                {
                    return CompiledConstructor([]);
                }

                if (reader.TokenType != JsonTokenType.StartObject)
                    throw new JsonException($"Expected a JSON object to build {typeof(T).Name}.");

                // Defaults are pre-boxed once; properties found in the JSON overwrite them.
                var parameterValues = (object?[])Defaults!.Clone();

                // Properties are read straight from the reader: no intermediate JsonDocument, no re-parsing.
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var index = IndexOf(ref reader);
                    reader.Read();
                    if (index < 0)
                        reader.Skip();
                    else
                        parameterValues[index] = JsonSerializer.Deserialize(ref reader, Parameters[index].ParameterType, options);
                }

                return CompiledConstructor(parameterValues);
            }

            private static int IndexOf(ref Utf8JsonReader reader)
            {
                var length = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
                Span<char> name = length <= 256 ? stackalloc char[256] : new char[length];
                var written = reader.CopyString(name);
                return IndexByName!.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name[..written], out var index) ? index : -1;
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
        }
    }
}