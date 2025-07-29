using System;
using System.Runtime.CompilerServices;
using Nez.Persistence;

namespace Nez;

/// <summary>
/// ComponentData class for Components. This is used to serialize Component data to JSON.
/// </summary>
public abstract class ComponentData { }

// Helper struct to store component type and its data as JSON
public struct ComponentDataEntry
{
	public string ComponentTypeName;
	public string ComponentName; // In case there are multiple components of the same type on an Entity, this is used to differentiate them.
	public string DataTypeName;
	public string Json;
}

/// <summary>
/// Execution order:
/// - OnAddedToEntity
/// - OnEnabled
///
/// Removal:
/// - OnRemovedFromEntity
///
/// </summary>
public class Component : IComparable<Component>
{
	/// <summary>
	/// the Entity this Component is attached to
	/// </summary>
	[JsonExclude]
	public Entity Entity;

	/// <summary>
	/// shortcut to entity.transform
	/// </summary>
	/// <value>The transform.</value>
	[JsonExclude]
	public Transform Transform
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => Entity.Transform;
	}

	public virtual ComponentData Data
	{
		get => null;
		set { }
	}

	/// <summary>
	/// true if the Component is enabled and the Entity is enabled. When enabled this Components lifecycle methods will be called.
	/// Changes in state result in onEnabled/onDisable being called.
	/// </summary>
	/// <value><c>true</c> if enabled; otherwise, <c>false</c>.</value>
	public bool Enabled
	{
		get => Entity != null ? Entity.Enabled && _enabled : _enabled;
		set => SetEnabled(value);
	}

	/// <summary>
	/// update order of the Components on this Entity
	/// </summary>
	/// <value>The order.</value>
	public int UpdateOrder
	{
		get => _updateOrder;
		set => SetUpdateOrder(value);
	}

	/// <summary>
	/// Show the desired name of the component in the ImGui inspector. If null, the type name will be used.
	/// </summary>
	public string Name { get; set; }

	private bool _enabled = true;

	internal int _updateOrder = 0;

	#region Component Lifecycle

	public Component(string name = null)
	{
		Name = name ?? GetType().Name;
	}

	/// <summary>
	/// called when this Component has had its Entity assigned but it is NOT yet added to the live Components list of the Entity yet. Useful
	/// for things like physics Components that need to access the Transform to modify collision body properties.
	/// </summary>
	public virtual void Initialize()
	{
	}

	/// <summary>
	/// Called when this component is added to a scene after all pending component changes are committed. At this point, the Entity field
	/// is set and the Entity.Scene is also set.
	/// </summary>
	public virtual void OnAddedToEntity()
	{
	}

	/// <summary>
	/// Called when this component is removed from its entity. Do all cleanup here.
	/// </summary>
	public virtual void OnRemovedFromEntity()
	{
	}

	/// <summary>
	/// called when the entity's position changes. This allows components to be aware that they have moved due to the parent
	/// entity moving.
	/// </summary>
	public virtual void OnEntityTransformChanged(Transform.Component comp)
	{
	}

	public virtual void DebugRender(Batcher batcher)
	{
	}

	/// <summary>
	/// called when the parent Entity or this Component is enabled
	/// </summary>
	public virtual void OnEnabled()
	{
	}

	/// <summary>
	/// called when the parent Entity or this Component is disabled
	/// </summary>
	public virtual void OnDisabled()
	{
	}

	#endregion

	#region Fluent setters

	public Component SetEnabled(bool isEnabled)
	{
		if (_enabled != isEnabled)
		{
			_enabled = isEnabled;

			if (_enabled)
				OnEnabled();
			else
				OnDisabled();
		}

		return this;
	}

	public Component SetUpdateOrder(int updateOrder)
	{
		if (_updateOrder != updateOrder)
		{
			_updateOrder = updateOrder;
			if (Entity != null)
				Entity.Components.MarkEntityListUnsorted();
		}

		return this;
	}

	#endregion

	/// <summary>
	/// creates a clone of this Component. The default implementation is just a MemberwiseClone so if a Component has object references
	/// that need to be cloned this method should be overriden.
	/// </summary>
	public virtual Component Clone()
	{
		var component = MemberwiseClone() as Component;
		component.Entity = null;

		return component;
	}

	public int CompareTo(Component other)
	{
		return _updateOrder.CompareTo(other._updateOrder);
	}


	public override string ToString()
	{
		var type = GetType();
		string typeName = type.IsGenericType
			? $"{type.BaseType.Name}<{type.GetGenericArguments()[0].Name}>"
			: type.Name;

		// If the component's name is null or empty, treat it as the type name
		var compName = string.IsNullOrEmpty(Name) ? typeName : Name;

		// Show only type if name matches type, otherwise show "Name (Type)"
		var displayName = compName == typeName ? typeName : $"{compName} ({typeName})";

		// Prepend entity name if available
		if (Entity != null && !string.IsNullOrEmpty(Entity.Name))
			return $"{Entity.Name}.{displayName}";
		else
			return displayName;
	}
}