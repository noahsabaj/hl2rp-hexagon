#nullable enable

using System;
using System.Linq;

namespace HL2RP.Logic;

/// <summary>Placement rules for a grid inventory. Mutates the document only when the move is legal.</summary>
public static class InventoryGrid
{
	public static bool Fits( InventoryData inventory, int x, int y, int width, int height, Guid? ignore = null )
	{
		if ( width < 1 || height < 1 || x < 0 || y < 0 ) return false;
		if ( x + width > inventory.Width || y + height > inventory.Height ) return false;
		return !inventory.Items.Any( other => other.Id != ignore &&
			x < other.X + other.Width && other.X < x + width &&
			y < other.Y + other.Height && other.Y < y + height );
	}

	/// <summary>Places the item in the first free cell, scanning rows then columns.</summary>
	public static Result<ItemStack> Add( InventoryData inventory, string definition, int width, int height )
	{
		for ( var y = 0; y < inventory.Height; y++ )
		for ( var x = 0; x < inventory.Width; x++ )
		{
			if ( !Fits( inventory, x, y, width, height ) ) continue;
			var item = new ItemStack { Id = Guid.NewGuid(), Definition = definition, X = x, Y = y, Width = width, Height = height };
			inventory.Items.Add( item );
			return Result<ItemStack>.Success( item );
		}
		return Result<ItemStack>.Fail( ErrorCode.Conflict, "There is no room for that." );
	}

	public static Result Move( InventoryData inventory, Guid itemId, int x, int y )
	{
		var item = inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( item is null ) return Result.Fail( ErrorCode.NotFound, "That item is not in this inventory." );
		if ( !Fits( inventory, x, y, item.Width, item.Height, ignore: itemId ) )
			return Result.Fail( ErrorCode.Conflict, "That space is taken." );
		item.X = x;
		item.Y = y;
		return Result.Success();
	}

	public static Result<ItemStack> Remove( InventoryData inventory, Guid itemId )
	{
		var item = inventory.Items.FirstOrDefault( value => value.Id == itemId );
		if ( item is null ) return Result<ItemStack>.Fail( ErrorCode.NotFound, "That item is not in this inventory." );
		inventory.Items.Remove( item );
		return Result<ItemStack>.Success( item );
	}
}
