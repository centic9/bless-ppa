// project created on 2/20/2006 at 5:30 PM
using System;
using BlessBuilder;

class MainClass
{
	public static void Main(string[] args)
	{
		ModuleTree mt = new ModuleTree("bless.mi");
		ModuleBuilder mb = new ModuleBuilder(mt);

		foreach (string option in args) {
			if (option.StartsWith("-"))
				mb.AddOption(option);
		}

		foreach (string moduleName in args) {
			if (moduleName.StartsWith("-"))
				continue;
			if (mb.Build(moduleName) == BuildStatus.Failed)
				System.Console.WriteLine("Build of module '{0}' failed!", moduleName);
		}
	}
}