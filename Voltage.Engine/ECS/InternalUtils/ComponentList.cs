using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Voltage.Utils.Collections;


namespace Voltage;

public class ComponentList : IEnumerable<Component>
{
	// global updateOrder sort for the IUpdatable list
	private static IUpdatableComparer compareUpdatableOrder = new();

	private Entity _entity;

	/// <summary>
	/// list of components added to the entity
	/// </summary>
	private FastList<Component> _components = new();

	/// <summary>
	/// list of all Components that want update called
	/// </summary>
	private FastList<IUpdatable> _updatableComponents = new();

	/// <summary>
	/// The list of components that were added this frame. Used to group the components so we can process them simultaneously
	/// </summary>
	internal List<Component> ComponentsToAdd = new();

	/// <summary>
	/// The list of components that were marked for removal this frame. Used to group the components so we can process them simultaneously
	/// </summary>
	private List<Component> _componentsToRemove = new();

	private List<Component> _tempBufferList = new();

	/// <summary>
	/// flag used to determine if we need to sort our Components this frame
	/// </summary>
	private bool _isComponentListUnsorted;

	public ComponentList(Entity entity)
	{
		_entity = entity;
	}


	#region array access

	public int Count => _components.Length;

	public Component this[int index] => _components.Buffer[index];

	#endregion


	public void MarkEntityListUnsorted()
	{
		_isComponentListUnsorted = true;
	}

	public void Add(Component component)
	{
		ComponentsToAdd.Add(component);
	}

	public void Remove(Component component)
	{
		Debug.WarnIf(_componentsToRemove.Contains(component),
			$"You are trying to remove a Component ({component}) that you already removed");

		// this may not be a live Component so we have to watch out for if it hasnt been processed yet but it is being removed in the same frame
		if (ComponentsToAdd.Contains(component))
		{
			ComponentsToAdd.Remove(component);
			return;
		}

		_componentsToRemove.Add(component);
	}

	/// <summary>Drops a removal queued this frame, so an undo issued before the commit keeps the component; true when one was pending.</summary>
	internal bool CancelRemove(Component component) => _componentsToRemove.Remove(component);

	/// <summary>
	/// removes all components from the component list immediately
	/// </summary>
	public void RemoveAllComponents()
	{
		for (var i = 0; i < _components.Length; i++)
			HandleRemove(_components.Buffer[i]);

		_components.Clear();
		_updatableComponents.Clear();
		ComponentsToAdd.Clear();
		_componentsToRemove.Clear();
	}

	internal void DeregisterAllComponents()
	{
		for (var i = 0; i < _components.Length; i++)
		{
			var component = _components.Buffer[i];

			if (component is RenderableComponent)
				_entity.Scene.RenderableComponents.Remove(component as RenderableComponent);

			if (component is IUpdatable)
				_updatableComponents.Remove(component as IUpdatable);
		}
	}

	internal void RegisterAllComponents()
	{
		for (var i = 0; i < _components.Length; i++)
		{
			var component = _components.Buffer[i];
			if (component is RenderableComponent)
				_entity.Scene.RenderableComponents.Add(component as RenderableComponent);

			if (component is IUpdatable)
				_updatableComponents.Add(component as IUpdatable);
		}
	}

	/// <summary>
	/// Moves all pending additions and removals into the live component lists 
	/// </summary>
	internal void CommitPendingAdditions()
	{
		if (_componentsToRemove.Count > 0)
		{
			for (var i = 0; i < _componentsToRemove.Count; i++)
			{
				HandleRemove(_componentsToRemove[i]);
				_components.Remove(_componentsToRemove[i]);
			}

			_componentsToRemove.Clear();
		}

		if (ComponentsToAdd.Count > 0)
		{
			for (int i = 0, count = ComponentsToAdd.Count; i < count; i++)
			{
				var component = ComponentsToAdd[i];

				if (component is RenderableComponent)
					_entity.Scene.RenderableComponents.Add(component as RenderableComponent);

				if (component is IUpdatable)
					_updatableComponents.Add(component as IUpdatable);

				_components.Add(component);
				_tempBufferList.Add(component);
			}

			ComponentsToAdd.Clear();
			_isComponentListUnsorted = true;
		}
	}

	// First calls all OnEnabled, and only after doing those calls OnStart to make sure that any OnStart is ALWAYS after OnEnabled
	internal void CallOnEnableAndOnStart()
	{
		var count = _tempBufferList.Count;

		for (var i = 0; i < count; i++)
		{
			var component = _tempBufferList[i];
			if (component == null)
			{
				Debug.Error($"A null component reference was found in _tempBufferList at index {i} on entity '{_entity.Name}'. Skipping.");
				continue;
			}

			try
			{
				if (component.Enabled)
					component.OnEnabled();
			}
			catch (Exception ex)
			{
				Debug.Error($"Exception in OnEnabled for component '{component.GetType().Name}' on entity '{_entity.Name}': {ex.Message}\n{ex.StackTrace}");
			}
		}

		for (var i = 0; i < count; i++)
		{
			var component = _tempBufferList[i];
			if (component == null)
				continue;

			try
			{
				if (component.Enabled)
					component.OnStart();
			}
			catch (Exception ex)
			{
				Debug.Error($"Exception in OnStart for component '{component.GetType().Name}' on entity '{_entity.Name}': {ex.Message}\n{ex.StackTrace}");
			}
		}

		_tempBufferList.Clear();

		if (_isComponentListUnsorted)
		{
			_updatableComponents.Sort(compareUpdatableOrder);
			_isComponentListUnsorted = false;
		}
	}

	/// <summary>
	/// handles any Components that need to be removed or added
	/// </summary>
	private void UpdateLists()
	{
		CommitPendingAdditions();
	}

	private void HandleRemove(Component component)
	{
		// deal with renderLayer list if necessary
		if (component is RenderableComponent)
			_entity.Scene.RenderableComponents.Remove(component as RenderableComponent);

		// deal with IUpdatable
		if (component is IUpdatable)
			_updatableComponents.Remove(component as IUpdatable);

		component.OnRemovedFromEntity();
		component.Entity = null;
	}

	/// <summary>
	/// Gets the first component of type T and returns it. Optionally skips checking un-initialized Components (Components who have not yet had their
	/// onAddedToEntity method called). If no components are found returns null.
	/// </summary>
	/// <param name="onlyReturnInitializedComponents">If set to <c>true</c> only return initialized components.</param>
	/// <typeparam name="T">The 1st type parameter.</typeparam>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public T GetComponent<T>(bool onlyReturnInitializedComponents) where T : class
	{
		for (var i = 0; i < _components.Length; i++)
		{
			var component = _components.Buffer[i];
			if (component is T)
				return component as T;
		}

		// we optionally check the pending components just in case addComponent and getComponent are called in the same frame
		if (!onlyReturnInitializedComponents)
			for (var i = 0; i < ComponentsToAdd.Count; i++)
			{
				var component = ComponentsToAdd[i];
				if (component is T)
					return component as T;
			}

		return null;
	}

	/// <summary>
	/// Gets all the components of type T without a List allocation
	/// </summary>
	/// <param name="components">Components.</param>
	/// <typeparam name="T">The 1st type parameter.</typeparam>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void GetComponents<T>(List<T> components) where T : class
	{
		for (var i = 0; i < _components.Length; i++)
		{
			var component = _components.Buffer[i];
			if (component is T)
				components.Add(component as T);
		}

		// we also check the pending components just in case addComponent and getComponent are called in the same frame
		for (var i = 0; i < ComponentsToAdd.Count; i++)
		{
			var component = ComponentsToAdd[i];
			if (component is T)
				components.Add(component as T);
		}
	}

	/// <summary>
	/// Gets all the components of type T. The returned List can be put back in the pool via ListPool.free.
	/// </summary>
	/// <typeparam name="T">The 1st type parameter.</typeparam>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public List<T> GetComponents<T>() where T : class
	{
		var components = ListPool<T>.Obtain();
		GetComponents(components);

		return components;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal void Update()
	{
		UpdateLists();

		// Components added mid-frame (e.g. from another component's OnStart) are committed
		// by UpdateLists() into _tempBufferList but CallOnEnableAndOnStart() is never reached
		// from EntityList — it only runs for newly added entities. Call it here so dynamically
		// added components get their OnEnabled/OnStart before their first Update().
		if (_tempBufferList.Count > 0)
			CallOnEnableAndOnStart();

		for (var i = 0; i < _updatableComponents.Length; i++)
		{
			var updatable = _updatableComponents.Buffer[i];

			if (updatable == null)
			{
				Debug.Error($"Null IUpdatable found at index {i} on entity '{_entity.Name}'.");
				continue;
			}

			var component = updatable as Component;

			bool shouldUpdate;
			if (Core.IsEditMode)
				// in EditMode only renderables update (so the scene stays visible while editing)
				shouldUpdate = component is RenderableComponent && updatable.Enabled && component.Enabled;
			else if (Core.IsPauseMode)
				// while paused, only UI components keep running; gameplay components are frozen
				shouldUpdate = component is IUpdatableInPauseMode && updatable.Enabled && component.Enabled;
			else
				shouldUpdate = (updatable.Enabled && component.Enabled) || (component is Collider collider && collider.IsVisibleEvenDisabled);

			if (!shouldUpdate)
				continue;

			try
			{
				updatable.Update();
			}
			catch (Exception ex)
			{
				Debug.Error(
					$"Exception in Update() on component '{component?.GetType().Name ?? "Unknown"}' " +
					$"(Name='{component?.Name}') on entity '{_entity.Name}':\n{ex}");
			}
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal void OnEntityTransformChanged(Transform.Component comp)
	{
		for (var i = 0; i < _components.Length; i++)
			if (_components.Buffer[i].Enabled)
				_components.Buffer[i].OnEntityTransformChanged(comp);

		for (var i = 0; i < ComponentsToAdd.Count; i++)
			if (ComponentsToAdd[i].Enabled)
				ComponentsToAdd[i].OnEntityTransformChanged(comp);
	}

	internal void OnEntityEnabled()
	{
		for (var i = 0; i < _components.Length; i++)
			if (_components.Buffer[i].Enabled)
				_components.Buffer[i].OnEnabled();
	}

	internal void OnEntityDisabled()
	{
		for (var i = 0; i < _components.Length; i++)
			_components.Buffer[i].OnDisabled();
	}

	internal void DebugRender(Batcher batcher)
	{
		for (var i = 0; i < _components.Length; i++)
		{
			// If the component is enabled or if it is a Collider that should be debug rendered even when disabled, render it
			if (_components.Buffer[i].Enabled || (_components.Buffer[i] is Collider collider && collider.IsVisibleEvenDisabled))
				_components.Buffer[i].DebugRender(batcher);
		}
	}


	#region IEnumerable<Component> Implementation

	public IEnumerator<Component> GetEnumerator()
	{
		for (int i = 0; i < _components.Length; i++)
			yield return _components.Buffer[i];
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}

	/// <summary>
	/// Get the list of components that were added this frame. This is useful for grouping components that were added
	/// </summary>
	/// <returns></returns>
	public List<Component> GetComponentsToAddList()
	{
		 return ComponentsToAdd;
	}

	#endregion

	/// <summary>
	/// Gets the first component of type T with the specified name and returns it. If no component is found returns null.
	/// </summary>
	/// <param name="name">Name of the component to find.</param>
	/// <typeparam name="T">The component type.</typeparam>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public T GetComponent<T>(string name) where T : class
	{
		for (var i = 0; i < _components.Length; i++)
		{
			var component = _components.Buffer[i];
			if (component is T && component.Name == name)
				return component as T;
		}

		// we also check the pending components just in case addComponent and getComponent are called in the same frame
		for (var i = 0; i < ComponentsToAdd.Count; i++)
		{
			var component = ComponentsToAdd[i];
			if (component is T && component.Name == name)
				return component as T;
		}

		return null;
	}
}