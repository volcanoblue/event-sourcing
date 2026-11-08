using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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

        /// <summary>
        /// Metadata resolver for reading. Types whose only constructors are parameterless and non-public cannot be
        /// created by System.Text.Json; this lets it use that constructor and then fill the fields and properties as usual.
        /// (Types with constructor parameters are handled by the converter.)
        /// </summary>
        public static IJsonTypeInfoResolver CreateResolver() =>
            new DefaultJsonTypeInfoResolver { Modifiers = { UsePrivateParameterlessConstructor } };

        private static void UsePrivateParameterlessConstructor(JsonTypeInfo info)
        {
            if (info.Kind != JsonTypeInfoKind.Object || info.CreateObject is not null || info.Type.IsAbstract)
                return;

            var constructors = info.Type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (constructors.Length == 0 || constructors.Any(c => c.GetParameters().Length > 0))
                return;

            var create = Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(constructors[0]), typeof(object))).Compile();
            info.CreateObject = create;
        }

        public override bool CanConvert(Type typeToConvert)
        {
            return CanConvertCache.GetOrAdd(typeToConvert, static type =>
            {
                var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                var hasPrivateConstructor = constructors.Any(c => !c.IsPublic);
                if (!hasPrivateConstructor)
                    return false;

                // Only parameterless constructors: nothing to map, see CreateResolver.
                if (constructors.All(c => c.GetParameters().Length == 0))
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
            private static readonly byte[][]? ExactNames;
            private static readonly ScalarKind[]? Kinds;

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
                    ExactNames = new byte[ParameterCount * 2][];
                    Kinds = new ScalarKind[ParameterCount];

                    for (int i = 0; i < ParameterCount; i++)
                    {
                        var paramName = Parameters[i].Name!;
                        Defaults[i] = Parameters[i].HasDefaultValue
                            ? Parameters[i].DefaultValue
                            : GetDefaultValue(Parameters[i].ParameterType);
                        IndexByName.TryAdd(paramName, i);
                        Kinds[i] = JsonScalars.KindOf(Parameters[i].ParameterType);

                        // The two spellings the serializer itself writes: camelCase and PascalCase.
                        ExactNames[2 * i] = System.Text.Encoding.UTF8.GetBytes(paramName);
                        ExactNames[2 * i + 1] = System.Text.Encoding.UTF8.GetBytes(
                            paramName.Length > 0 ? char.ToUpperInvariant(paramName[0]) + paramName[1..] : paramName);
                    }

                    // Compile expression tree for fast constructor invocation
                    CompiledConstructor = BuildConstructorDelegate();
                }
            }

            private static Func<object?[], T> BuildConstructorDelegate()
            {
                if (Constructor is null || Parameters is null)
                    throw new InvalidOperationException("Constructor not found");

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

            // One converter instance exists per options instance, so the metadata can be resolved once here.
            private JsonTypeInfo[]? _parameterInfos;

            public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Null)
                    return default;

                if (Constructor is null || Parameters is null || CompiledConstructor is null)
                    return default;

                if (reader.TokenType != JsonTokenType.StartObject)
                    throw new JsonException($"Expected a JSON object to build {typeof(T).Name}.");

                // Defaults are pre-boxed once; properties found in the JSON overwrite them.
                var parameterValues = (object?[])Defaults!.Clone();
                var infos = _parameterInfos ??= Array.ConvertAll(Parameters, p => options.GetTypeInfo(p.ParameterType));

                // Properties are read straight from the reader: no intermediate JsonDocument, no re-parsing.
                // Properties are written in parameter order, so the next one is usually the next parameter.
                var next = 0;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var index = IndexOf(ref reader, ref next);
                    reader.Read();
                    if (index < 0)
                        reader.Skip();
                    else
                        parameterValues[index] = JsonScalars.Read(ref reader, Kinds![index], infos[index]);
                }

                return CompiledConstructor(parameterValues);
            }

            private static int IndexOf(ref Utf8JsonReader reader, ref int next)
            {
                for (var n = 0; n < ParameterCount; n++)
                {
                    var i = next + n;
                    if (i >= ParameterCount)
                        i -= ParameterCount;

                    if (reader.ValueTextEquals(ExactNames![2 * i]) || reader.ValueTextEquals(ExactNames[2 * i + 1]))
                    {
                        next = i + 1;
                        return i;
                    }
                }

                // Any other casing (or an unknown property).
                var length = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
                Span<char> name = length <= 128 ? stackalloc char[128] : new char[length];
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