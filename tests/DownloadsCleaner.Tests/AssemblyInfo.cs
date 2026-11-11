using Xunit;

// Тесты локализации меняют глобальный язык интерфейса, поэтому наборы тестов
// не должны выполняться параллельно друг другу.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
