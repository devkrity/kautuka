// This executable is intentionally fail-closed until the host-side protocol is ported.
Console.Error.WriteLine("Kautuka OCI hook is incomplete; refusing container creation.");
return 1;
