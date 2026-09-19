#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace HL2RP.Logic;

/// <summary>
/// Every character on the server, held in memory and written through to the store. A roleplay
/// server has thousands of characters at most, so the whole set is the index.
/// </summary>
public sealed class CharacterRoster
{
	public const int MaximumSlots = 5;
	public const long StartingTokens = 100;

	private readonly DocumentStore _store;
	private readonly Dictionary<Guid, CharacterData> _characters = new();
	// A name's key never changes, so it is computed once when the character enters the roster.
	private readonly Dictionary<string, Guid> _nameKeys = new( StringComparer.Ordinal );

	public CharacterRoster( DocumentStore store )
	{
		_store = store ?? throw new ArgumentNullException( nameof(store) );
		foreach ( var character in _store.LoadAll<CharacterData>( "characters" ) )
		{
			_characters[character.Id] = character;
			_nameKeys[NameRules.Key( character.Name )] = character.Id;
		}
	}

	public int Count => _characters.Count;

	public CharacterData? Find( Guid id ) => _characters.TryGetValue( id, out var character ) ? character : null;

	public IReadOnlyList<CharacterData> OwnedBy( long steamId ) =>
		_characters.Values.Where( value => value.SteamId == steamId ).OrderBy( value => value.CreatedAt ).ToArray();

	public Result<CharacterData> Create( long steamId, string? name, string? description, string faction, DateTimeOffset now )
	{
		var cleanName = NameRules.NormalizeName( name );
		if ( !cleanName.Ok ) return Result<CharacterData>.Fail( cleanName.Code, cleanName.Message );
		var cleanDescription = NameRules.NormalizeDescription( description );
		if ( !cleanDescription.Ok ) return Result<CharacterData>.Fail( cleanDescription.Code, cleanDescription.Message );
		if ( OwnedBy( steamId ).Count >= MaximumSlots )
			return Result<CharacterData>.Fail( ErrorCode.Conflict, $"You already have {MaximumSlots} characters." );
		var key = NameRules.Key( cleanName.Value );
		if ( key.Length == 0 ) return Result<CharacterData>.Fail( ErrorCode.Invalid, "Name needs at least one letter or digit." );
		if ( _nameKeys.ContainsKey( key ) )
			return Result<CharacterData>.Fail( ErrorCode.Conflict, "Another character already uses this name or one that reads like it." );

		var character = new CharacterData
		{
			Id = Guid.NewGuid(),
			SteamId = steamId,
			Name = cleanName.Value,
			Description = cleanDescription.Value,
			Faction = faction,
			Tokens = StartingTokens,
			CreatedAt = now
		};
		_store.Save( Path( character.Id ), character );
		_characters[character.Id] = character;
		_nameKeys[key] = character.Id;
		return Result<CharacterData>.Success( character );
	}

	/// <summary>Writes the character's current state. Call after every change the player should keep.</summary>
	public void Save( CharacterData character ) => _store.Save( Path( character.Id ), character );

	public Result Delete( long steamId, Guid id )
	{
		// Same answer for "not yours" and "does not exist", so ids cannot be probed.
		if ( Find( id ) is not { } character || character.SteamId != steamId )
			return Result.Fail( ErrorCode.NotFound, "Character was not found." );
		_store.Delete( Path( id ) );
		_characters.Remove( id );
		_nameKeys.Remove( NameRules.Key( character.Name ) );
		return Result.Success();
	}

	private static string Path( Guid id ) => $"characters/{id:N}.json";
}
