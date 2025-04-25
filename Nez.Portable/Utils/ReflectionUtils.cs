using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Nez.Utils.Extensions;

namespace Nez.Utils
{
	/// <summary>
	/// helper class to fetch property delegates
	/// </summary>
	public static class ReflectionUtils
	{
		#region Fields

		public static FieldInfo GetFieldInfo(object targetObject, string fieldName) => GetFieldInfo(targetObject.GetType(), fieldName);

		
		public static FieldInfo GetFieldInfo(
			[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)]
			Type type,
			string fieldName
		)
		{
			FieldInfo fieldInfo = null;
			do
			{
				fieldInfo = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				type = type.BaseType;
			} while (fieldInfo == null && type != null);


			return fieldInfo;
		}

		public static IEnumerable<FieldInfo> GetFields(
			[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields |
										DynamicallyAccessedMemberTypes.NonPublicFields)]
			Type type
		) => type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

		public static object GetFieldValue(object targetObject, string fieldName)
			=> GetFieldInfo(targetObject, fieldName).GetValue(targetObject);

		#endregion

		#region Properties

		public static PropertyInfo GetPropertyInfo(object targetObject, string propertyName)
			=> GetPropertyInfo(targetObject.GetType(), propertyName);

		public static PropertyInfo GetPropertyInfo(
			[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties |
										DynamicallyAccessedMemberTypes.NonPublicProperties)]
			Type type,
			string propertyName
		)
		{
			return type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
		}

		public static IEnumerable<PropertyInfo> GetProperties(
			[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties |
										DynamicallyAccessedMemberTypes.NonPublicProperties)]
			Type type
		) => type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

		// Add annotations for getter/setter methods
		public static MethodInfo GetPropertyGetter(
			PropertyInfo prop
		) => prop.GetGetMethod(true);

		public static MethodInfo GetPropertySetter(
			PropertyInfo prop
		) => prop.GetSetMethod(true);

		public static object GetPropertyValue(object targetObject, string propertyName)
		{
			var propInfo = GetPropertyInfo(targetObject, propertyName);
			var methodInfo = GetPropertyGetter(propInfo);
			return methodInfo.Invoke(targetObject, Array.Empty<object>());
		}

		public static T SetterForProperty<T>(
			object targetObject,
			string propertyName
		) => CreateDelegate<T>(targetObject, GetPropertyInfo(targetObject, propertyName).SetMethod);

		public static T GetterForProperty<T>(
			object targetObject,
			string propertyName
		) => CreateDelegate<T>(targetObject, GetPropertyInfo(targetObject, propertyName).GetMethod);

		#endregion

		#region Methods

		[RequiresUnreferencedCode("Reflection-based method access. Ensure methods are preserved in AOT.")]
		public static IEnumerable<MethodInfo> GetMethods(
			[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods |
										DynamicallyAccessedMemberTypes.NonPublicMethods)]
			Type type
		) => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

		public static MethodInfo GetMethodInfo(object targetObject, string methodName)
			=> GetMethodInfo(targetObject.GetType(), methodName);

		public static MethodInfo GetMethodInfo(object targetObject, string methodName, Type[] parameters)
			=> GetMethodInfo(targetObject.GetType(), methodName, parameters);

		public static MethodInfo GetMethodInfo(
			[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods |
										DynamicallyAccessedMemberTypes.NonPublicMethods)]
			Type type,
			string methodName,
			Type[] parameters = null
		)
		{
			return parameters == null
				? type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
				: type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
					null, parameters, null);
		}

		#endregion

		public static T CreateDelegate<T>(
			object targetObject,
			MethodInfo methodInfo
		) => (T)(object)Delegate.CreateDelegate(typeof(T), targetObject, methodInfo);


		/// <summary>
		/// gets all subclasses of <paramref name="baseClassType"> optionally filtering only for those with
		/// a parameterless constructor. Abstract Types will not be returned.
		/// </summary>
		/// <param name="baseClassType"></param>
		/// <param name="onlyIncludeParameterlessConstructors"></param>
		/// <returns></returns>
		[RequiresUnreferencedCode("Assembly scanning is not AOT-safe. Use explicit type registration instead.")]
		public static List<Type> GetAllSubclasses(Type baseClassType, bool onlyIncludeParameterlessConstructors = false) // Mark assembly-scanning methods as unsafe for AOT
		{
			var typeList = new List<Type>();
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				foreach (var type in assembly.GetTypes())
				{
					if (type.IsSubclassOf(baseClassType) && !type.IsAbstract)
					{
						if (onlyIncludeParameterlessConstructors)
						{
							if (type.GetConstructor(Type.EmptyTypes) == null)
							{
								Debug.Log("no go: " + type.Name);
								continue;
							}
						}

						typeList.Add(type);
					}
				}
			}

			return typeList;
		}

		/// <summary>
		/// gets all Types assignable from <paramref name="baseClassType"> optionally filtering only for those with
		/// a parameterless constructor. Abstract Types will not be returned.
		/// </summary>
		/// <param name="baseClassType"></param>
		/// <param name="onlyIncludeParameterlessConstructors"></param>
		/// <returns></returns>
		[RequiresUnreferencedCode("Assembly scanning is not AOT-safe. Use explicit type registration instead.")]
		public static List<Type> GetAllTypesAssignableFrom(
			Type baseClassType,
			bool onlyIncludeParameterlessConstructors = false)
		{
			var typeList = new List<Type>();
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				foreach (var type in assembly.GetTypes())
				{
					if (baseClassType.IsAssignableFrom(type) && !type.IsAbstract)
					{
						if (onlyIncludeParameterlessConstructors)
						{
							if (type.GetConstructor(Type.EmptyTypes) == null)
								continue;
						}

						typeList.Add(type);
					}
				}
			}

			return typeList;
		}

		/// <summary>
		/// checks <paramref name="type"/> to see if it or any base class in the chain IsGenericType
		/// </summary>
		/// <param name="type"></param>
		/// <returns></returns>
		public static bool IsGenericTypeOrSubclassOfGenericType(Type type)
		{
			var currentType = type;
			while (currentType != null && currentType != typeof(object))
			{
				if (currentType.IsGenericType)
					return true;

				currentType = currentType.BaseType;
			}

			return false;
		}

		[RequiresUnreferencedCode("Assembly scanning is not AOT-safe. Use explicit type registration instead.")]
		public static List<Type> GetAllTypesWithAttribute<T>() where T : Attribute
		{
			var typeList = new List<Type>();
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				foreach (var type in assembly.GetTypes())
				{
					if (type.GetAttribute<T>() != null)
						typeList.Add(type);
				}
			}
			return typeList;
		}
	}
}