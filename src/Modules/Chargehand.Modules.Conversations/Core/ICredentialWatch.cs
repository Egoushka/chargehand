namespace Chargehand.Modules.Conversations.Core;

/// <summary>What a conversation tells the pause on its runtime's credential.</summary>
internal interface ICredentialWatch
{
    /// <summary>The conversation hit an error; the pause takes it if it says the provider rejected the credential.</summary>
    void Rejected(string error);

    /// <summary>A turn ended the way a working credential's turn does.</summary>
    void Worked();
}
