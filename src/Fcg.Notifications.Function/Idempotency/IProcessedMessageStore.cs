namespace Fcg.Notifications.Function.Idempotency;

/// <summary>
/// Registra e consulta chaves naturais de eventos já processados, garantindo que o mesmo evento
/// não gere e-mails duplicados.
/// </summary>
/// <remarks>
/// <para>
/// <b>A implementação DEVE ser durável e compartilhada entre réplicas.</b> Uma implementação em
/// memória não serve neste repositório — e é justamente a que o <c>notifications-api</c> usava.
/// Lá funcionava, porque era um container fixo com uma réplica. Aqui não:
/// </para>
/// <list type="number">
///   <item>
///     a Function <b>escala a zero</b>: o KEDA termina o pod quando a fila esvazia, e um
///     dicionário em memória morre junto. Uma reentrega do mesmo evento chega num processo novo,
///     com memória vazia, e reenvia o e-mail;
///   </item>
///   <item>
///     <c>maxReplicaCount: 5</c>: sob rajada existem até cinco processos, cada um com seu próprio
///     dicionário, sem nenhum enxergar o do outro.
///   </item>
/// </list>
/// <para>
/// <b>A operação também DEVE ser atômica</b> — test-and-set numa única ida ao servidor. "Consultar
/// e depois gravar" tem janela de corrida entre réplicas concorrentes: as duas passam pelo
/// <c>Exists</c> e as duas enviam.
/// </para>
/// </remarks>
public interface IProcessedMessageStore
{
    /// <summary>
    /// Tenta marcar a chave como processada. Retorna <c>true</c> se a chave
    /// era inédita (PRIMEIRA vez) e <c>false</c> se já existia.
    /// A operação deve ser atômica para evitar corrida entre reentregas concorrentes.
    /// </summary>
    Task<bool> TryMarkAsProcessedAsync(string messageType, string naturalKey, CancellationToken ct = default);
}
