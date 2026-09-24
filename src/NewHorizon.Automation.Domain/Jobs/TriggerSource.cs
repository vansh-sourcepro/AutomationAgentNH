namespace NewHorizon.Automation.Domain.Jobs;

/// <summary>
/// What asked for a run. Recorded once per run rather than once per job, because it is a property
/// of the invocation and not of any document it touched.
/// </summary>
public enum TriggerSource
{
    /// <summary>A direct call to the agent's own API — the ERP UI, a script, curl.</summary>
    Api = 0,

    /// <summary>A person asked for it in so many words, through whatever front end took the words.</summary>
    UserPrompt = 1,

    /// <summary>A conversational agent acting for a person. <c>TriggerReference</c> carries the session.</summary>
    Chatbot = 2,

    /// <summary>
    /// A scheduled sweep. Present so the vocabulary is complete: nothing in the agent emits this
    /// for indent → PO today, and the hosted service that once did was deliberately deleted.
    /// </summary>
    Timer = 3,

    /// <summary>The ERP pushed a document at the agent.</summary>
    ErpPush = 4,

    /// <summary>The safety-net poll that looks for documents no job ever started on.</summary>
    Reconcile = 5,

    /// <summary>An operator re-running something by hand, including a retry.</summary>
    Manual = 6,
}
