using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Nez.Data
{
	[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
	public class EntityData : Component
	{
		[HideAttributeInInspector]
		public List<ComponentDataEntry> ComponentDataList;

		public EntityData()
		{
			ComponentDataList = new List<ComponentDataEntry>();
		}
	}
}
