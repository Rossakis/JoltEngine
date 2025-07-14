using System;

public interface IOneShotDelegate { }

public class OneShotDelegate<T> : IOneShotDelegate
{
	private readonly Action<T> _action;
	private bool _invoked = false;

	public OneShotDelegate(Action<T> action)
	{
		_action = action;
	}

	public void Invoke(T entity)
	{
		if (!_invoked)
		{
			_invoked = true;
			_action(entity);
		}
	}
}