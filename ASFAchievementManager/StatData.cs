namespace ASFAchievementManager {
	public class StatData {
		public uint StatNum { get; set; }
		public int BitNum { get; set; }
		public bool IsSet { get; set; }
		public bool Restricted { get; set; }
		public uint Dependency { get; set; }
		public uint DependencyValue { get; set; }
		public string DependencyName { get; set; }
		public string Name { get; set; }
	}
}
