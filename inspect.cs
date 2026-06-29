using System;
using System.Reflection;

class Program
{
    static void Main(string[] args)
    {
        try
        {
            // Resolve assembly loading for dependencies in the game directory
            AppDomain.CurrentDomain.AssemblyResolve += (sender, resolveArgs) =>
            {
                string name = resolveArgs.Name.Split(',')[0] + ".dll";
                string path = System.IO.Path.Combine(@"C:\Program Files (x86)\Steam\steamapps\common\Gamble With Your Friends\Gamble With Your Friends_Data\Managed", name);
                if (System.IO.File.Exists(path))
                {
                    return Assembly.LoadFrom(path);
                }
                return null;
            };

            var asm = Assembly.LoadFrom("GambleDumbMenu.dll");
            Console.WriteLine("Loaded assembly: " + asm.FullName);
            foreach (var t in asm.GetTypes())
            {
                Console.WriteLine("Type: Name='{0}', Namespace='{1}', FullName='{2}'", t.Name, t.Namespace, t.FullName);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex);
        }
    }
}
