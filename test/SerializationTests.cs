using System.Text.Json;
using System.Text.Json.Nodes;
using VolcanoBlue.EventSourcing.Abstractions;
using VolcanoBlue.EventSourcing.Serialization;

namespace VolcanoBlue.EventSourcing.Tests;

public sealed record Address(string City, int Zip);

/// <summary>Private constructor with parameters, some with defaults: read through PrivateConstructorConverter.</summary>
public sealed class CtorEvent : IEvent
{
    public long EventId { get; }
    public string Name { get; }
    public int Count { get; }
    public Address? Address { get; }
    public string[] Tags { get; }

    private CtorEvent(long eventId, string name, int count = 5, Address? address = null, string[]? tags = null)
    {
        EventId = eventId;
        Name = name;
        Count = count;
        Address = address;
        Tags = tags ?? [];
    }

    public static CtorEvent Create(long eventId, string name, int count, Address? address, params string[] tags) =>
        new(eventId, name, count, address, tags);
}

/// <summary>Only a private parameterless constructor: state lives in fields.</summary>
public sealed class ParameterlessPrivateEvent : IEvent
{
    public long EventId { get; set; }
    public string Note = "";

    private ParameterlessPrivateEvent() { }

    public static ParameterlessPrivateEvent Create(long eventId, string note) => new() { EventId = eventId, Note = note };
}

public abstract class BaseNamed : EventSourcedEntity
{
    private string _name = "base";

    public string BaseName => _name;

    public void SetBase(string name) => _name = name;
}

/// <summary>Declares a field with the same name as one of its base class.</summary>
public sealed class DerivedNamed : BaseNamed, ISnapshot
{
    private string _name = "derived";

    public string DerivedName => _name;
    public long EntityVersion => Version;

    private DerivedNamed() { }

    public static DerivedNamed Create() => new();

    public void SetDerived(string name) => _name = name;

    protected override void ProcessEvent(IEvent @event) { }
}

/// <summary>One parameter of every scalar type that has a fast read path.</summary>
public sealed class ScalarsEvent : IEvent
{
    public long EventId { get; }
    public string? Text { get; }
    public int Int { get; }
    public bool Flag { get; }
    public decimal Money { get; }
    public double Ratio { get; }
    public Guid Id { get; }
    public DateTime Day { get; }
    public DateTimeOffset At { get; }
    public int? Maybe { get; }

    private ScalarsEvent(long eventId, string? text, int @int, bool flag, decimal money, double ratio, Guid id, DateTime day, DateTimeOffset at, int? maybe)
    {
        EventId = eventId; Text = text; Int = @int; Flag = flag; Money = money; Ratio = ratio; Id = id; Day = day; At = at; Maybe = maybe;
    }

    public static ScalarsEvent Create(string? text, int? maybe) =>
        new(long.MaxValue, text, int.MinValue, true, 79228162514264337593543950335m, 0.1 + 0.2, Guid.NewGuid(),
            new DateTime(2026, 11, 8, 15, 0, 0, DateTimeKind.Utc), new DateTimeOffset(2026, 11, 8, 15, 0, 0, TimeSpan.FromHours(-3)), maybe);
}

public sealed class SerializationTests
{
    private readonly SerializationRuntime _runtime = new([typeof(CtorEvent), typeof(ParameterlessPrivateEvent), typeof(ScalarsEvent)], TimeProvider.System);

    private static Event Row(Type type, string json) => new(1, "s-1", "2026-01-01 00:00:00", type.FullName!, json);

    private IEvent Read(Type type, string json) => _runtime.Events.Deserialize(Row(type, json));

    [Fact]
    public void Event_WithPrivateConstructor_RoundTrips()
    {
        var original = CtorEvent.Create(3, "n", 9, new Address("Rio", 123), "a", "b");

        var row = _runtime.Events.Serialize("s-1", original, "2026-01-01 00:00:00");
        var loaded = (CtorEvent)_runtime.Events.Deserialize(row);

        Assert.Equal(3, loaded.EventId);
        Assert.Equal("n", loaded.Name);
        Assert.Equal(9, loaded.Count);
        Assert.Equal(new Address("Rio", 123), loaded.Address);
        Assert.Equal(["a", "b"], loaded.Tags);
    }

    [Fact]
    public void Event_PropertyNamesAreCaseInsensitive_AndOrderDoesNotMatter()
    {
        var loaded = (CtorEvent)Read(typeof(CtorEvent), """{"NAME":"x","count":2,"eventid":4}""");

        Assert.Equal(4, loaded.EventId);
        Assert.Equal("x", loaded.Name);
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public void Event_MissingProperties_UseDefaults()
    {
        var loaded = (CtorEvent)Read(typeof(CtorEvent), """{"EventId":1}""");

        Assert.Null(loaded.Name);
        Assert.Equal(5, loaded.Count); // declared default
        Assert.Null(loaded.Address);
        Assert.Empty(loaded.Tags);
    }

    [Fact]
    public void Event_UnknownProperties_AreSkipped_WhateverTheirShape()
    {
        const string json = """
            {"Extra":{"a":[1,2,{"b":null}],"c":"d"},"EventId":2,"List":[1,[2,3]],"Name":"kept","Flag":true,"Count":7}
            """;

        var loaded = (CtorEvent)Read(typeof(CtorEvent), json);

        Assert.Equal(2, loaded.EventId);
        Assert.Equal("kept", loaded.Name);
        Assert.Equal(7, loaded.Count);
    }

    [Fact]
    public void Event_PropertiesAfterANestedObject_AreStillRead()
    {
        var loaded = (CtorEvent)Read(typeof(CtorEvent), """{"Address":{"City":"Rio","Zip":1},"Name":"after","EventId":8}""");

        Assert.Equal(new Address("Rio", 1), loaded.Address);
        Assert.Equal("after", loaded.Name);
        Assert.Equal(8, loaded.EventId);
    }

    [Theory]
    [InlineData("""{"EventId":1,"Name":"cut""")]
    [InlineData("""{"EventId":""")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public void Event_MalformedJson_ThrowsJsonException(string json) =>
        Assert.ThrowsAny<JsonException>(() => Read(typeof(CtorEvent), json));

    [Fact]
    public void Event_WithPrivateParameterlessConstructor_KeepsItsFields()
    {
        var original = ParameterlessPrivateEvent.Create(6, "note");

        var row = _runtime.Events.Serialize("s-1", original, "2026-01-01 00:00:00");
        var loaded = (ParameterlessPrivateEvent)_runtime.Events.Deserialize(row);

        Assert.Equal(6, loaded.EventId);
        Assert.Equal("note", loaded.Note);
    }

    [Fact]
    public void Event_NullData_ThrowsJsonException() =>
        Assert.Throws<JsonException>(() => Read(typeof(CtorEvent), "null"));

    private static string Snapshot()
    {
        var entity = EncapsulatedEntity.Create();
        entity.Rename("a");
        entity.Rename("b");
        return EntityStateSerializer.Serialize(entity);
    }

    [Fact]
    public void Snapshot_RoundTrips_AndRestoresTheVersion()
    {
        var loaded = EntityStateSerializer.Deserialize<EncapsulatedEntity>(Snapshot(), 2);

        Assert.Equal(["a", "b"], loaded.Names);
        Assert.Equal(2, loaded.Renames);
        Assert.Equal("b", loaded.Last);
        Assert.Equal(2, loaded.Version);
    }

    [Fact]
    public void Snapshot_FieldOrderInTheJsonDoesNotMatter_AndUnknownFieldsAreSkipped()
    {
        var node = JsonNode.Parse(Snapshot())!.AsObject();
        var reordered = new JsonObject { ["unknown"] = JsonNode.Parse("""{"x":[1,{"y":2}]}""") };
        foreach (var property in node.Reverse())
            reordered[property.Key] = property.Value!.DeepClone();
        reordered["trailing"] = 1;

        var loaded = EntityStateSerializer.Deserialize<EncapsulatedEntity>(reordered.ToJsonString(), 2);

        Assert.Equal(["a", "b"], loaded.Names);
        Assert.Equal("b", loaded.Last);
        Assert.Equal(2, loaded.Renames);
    }

    [Fact]
    public void Snapshot_MissingFields_KeepTheirInitializers()
    {
        var loaded = EntityStateSerializer.Deserialize<EncapsulatedEntity>("{}", 0);

        Assert.Empty(loaded.Names);
        Assert.Equal(7, loaded.Skipped);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"_renames":""")]
    [InlineData("")]
    public void Snapshot_MalformedJson_ThrowsJsonException(string json) =>
        Assert.ThrowsAny<JsonException>(() => EntityStateSerializer.Deserialize<EncapsulatedEntity>(json, 1));

    [Fact]
    public void Snapshot_ComputedPropertiesAreNotPersisted()
    {
        var json = JsonNode.Parse(Snapshot())!.AsObject();

        Assert.DoesNotContain("Names", json.Select(p => p.Key));
        Assert.DoesNotContain("Renames", json.Select(p => p.Key));
        Assert.DoesNotContain("EntityVersion", json.Select(p => p.Key));
    }

    [Fact]
    public void Snapshot_FieldsWithTheSameNameInBaseAndDerivedClasses_KeepTheirOwnValues()
    {
        var entity = DerivedNamed.Create();
        entity.SetBase("b1");
        entity.SetDerived("d1");

        var loaded = EntityStateSerializer.Deserialize<DerivedNamed>(EntityStateSerializer.Serialize(entity), 0);

        Assert.Equal("b1", loaded.BaseName);
        Assert.Equal("d1", loaded.DerivedName);
    }

    [Theory]
    [InlineData("plain", null)]
    [InlineData("quote \" backslash \\ newline \n tab \t unicode \u00e9 \ud83d\ude00 </script>", 5)]
    [InlineData(null, null)]
    [InlineData("", 0)]
    public void Event_ScalarParameters_RoundTripExactly(string? text, int? maybe)
    {
        var original = ScalarsEvent.Create(text, maybe);

        var row = _runtime.Events.Serialize("s-1", original, "2026-01-01 00:00:00");
        var loaded = (ScalarsEvent)_runtime.Events.Deserialize(row);

        Assert.Equal(original.EventId, loaded.EventId);
        Assert.Equal(original.Text, loaded.Text);
        Assert.Equal(original.Int, loaded.Int);
        Assert.Equal(original.Flag, loaded.Flag);
        Assert.Equal(original.Money, loaded.Money);
        Assert.Equal(original.Ratio, loaded.Ratio);
        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.Day, loaded.Day);
        Assert.Equal(original.At, loaded.At);
        Assert.Equal(original.At.Offset, loaded.At.Offset);
        Assert.Equal(original.Maybe, loaded.Maybe);
    }

    [Theory]
    [InlineData("""{"EventId":1,"Int":"7"}""")]      // a number written as a string
    [InlineData("""{"EventId":1,"Flag":"true"}""")]
    [InlineData("""{"EventId":1,"Int":1.5}""")]
    [InlineData("""{"EventId":1,"Id":"not-a-guid"}""")]
    [InlineData("""{"EventId":1,"Day":"yesterday"}""")]
    [InlineData("""{"EventId":1,"Text":5}""")]
    public void Event_ScalarOfTheWrongShape_ThrowsJsonException(string json) =>
        Assert.ThrowsAny<JsonException>(() => Read(typeof(ScalarsEvent), json));
}
