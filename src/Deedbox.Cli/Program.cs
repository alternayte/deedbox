using System.CommandLine;

var root = new RootCommand("Deedbox command-line tool.");
return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
