﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Raven.Abstractions.Data;
using Raven.Bundles.Encryption.Settings;
using Raven.Database.Plugins;
using Raven.Json.Linq;

namespace Raven.Bundles.Encryption.Plugin
{
	public class EncryptionSettingsPutTrigger : AbstractPutTrigger
	{
		private EncryptionSettings settings;

		public override void Initialize()
		{
			settings = EncryptionSettingsManager.GetEncryptionSettingsForDatabase(Database);
		}

		public override VetoResult AllowPut(string key, RavenJObject document, RavenJObject metadata, TransactionInformation transactionInformation)
		{
			if (key == Constants.InDatabaseKeyVerificationDocumentName)
			{
				if (Database == null) // we haven't been called yet
					return VetoResult.Allowed;

				if (Database.Get(key, null) != null)
					return VetoResult.Deny("The encryption verification document already exists and cannot be overwritten.");
			}

			return VetoResult.Allowed;
		}
	}
}