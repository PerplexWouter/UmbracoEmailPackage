﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Umbraco.Forms.Core;
using Umbraco.Forms.Core.Enums;
using System.Text.RegularExpressions;
using System.IO;
using Umbraco.Forms.Data.Storage;
using System.Web.Hosting;

namespace PerplexMail.UmbracoForms
{
    public class EmailWorkflow : WorkflowType
    {
        public EmailWorkflow()
        {
            Name = "Send PerplexMail";
            Id = new Guid("A6A2C4F6-CF89-11DE-1045-5BB025383108");
            Description = "Sends a PerplexMail Email";
        }

        [Umbraco.Forms.Core.Attributes.Setting("Email", description = "The email that should be sent. In the email, use the tag [#formsautogenerate#] to list all field labels and values automatically. To reference the value of a specific field, use [#<label>#] or [#<alias>#]. E.g, if a field label is \"First name\" (with alias \"firstname\"), use either [#First name#] or [#firstname#] to render its value.", view = "Pickers.Content")]
        public string Email { get; set; }

        [Umbraco.Forms.Core.Attributes.Setting("Attachments", description = "If checked, uploaded files will be added to the e-mail as attachments", view = "CheckBox")]
        public string Attachments { get; set; }
      
        // GUIDs of all existing field types and our own. As far as I know we cannot obtain these
        // via some API so just drop them here.
        private readonly Guid Guid_Checkbox = new Guid("D5C0C390-AE9A-11DE-A69E-666455D89593");
        private readonly Guid Guid_CheckboxList = new Guid("FAB43F20-A6BF-11DE-A28F-9B5755D89593");
        private readonly Guid Guid_DatePicker = new Guid("F8B4C3B8-AF28-11DE-9DD8-EF5956D89593");
        private readonly Guid Guid_DropDownList = new Guid("0DD29D42-A6A5-11DE-A2F2-222256D89593");
        private readonly Guid Guid_FileUpload = new Guid("84A17CF8-B711-46a6-9840-0E4A072AD000");
        private readonly Guid Guid_HiddenField = new Guid("DA206CAE-1C52-434E-B21A-4A7C198AF877");
        private readonly Guid Guid_Password = new Guid("FB37BC60-D41E-11DE-AEAE-37C155D89593");
        private readonly Guid Guid_RadioButtonList = new Guid("903DF9B0-A78C-11DE-9FC1-DB7A56D89593");
        private readonly Guid Guid_Recaptcha = new Guid("4A2E8E12-9613-4720-9BCD-F9871262D6AC");
        private readonly Guid Guid_Text = new Guid("e3fbf6c4-f46c-495e-aff8-4b3c227b4a98");
        private readonly Guid Guid_TextArea = new Guid("023F09AC-1445-4bcb-B8FA-AB49F33BD046");
        private readonly Guid Guid_TextField = new Guid("3F92E01B-29E2-4a30-BF33-9DF5580ED52C");
        private readonly Guid Guid_PerplexImageUpload = new Guid("11fff56b-7e0e-4bfc-97ba-b5126158d33d");
        private readonly Guid Guid_PerplexFileUpload = new Guid("3e170f26-1fcb-4f60-b5d2-1aa2723528fd");

        public override WorkflowExecutionStatus Execute(Record record, RecordEventArgs e)
        {
            var emailTags = new List<EmailTag>();

            // All fields of the form as list of EmailTag
            List<EmailTag> allTags = record
                .RecordFields
                .Values
                .SelectMany(ParseRecordField)
                .ToList();     

            emailTags.AddRange(allTags);

            // There is one special tag which includes all field labels + contents
            var autogeneratedForm = string.Join("<br/><br/>", allTags
                // The autogenerated tag should use the Captions (to render in the email),
                // not the aliases. These are every second element in the list. Not a big fan of this
                // code, having functionality like this rely on index/order in the given list.
                // Possible refactor target in the future.
                .Where((et, idx) => idx % 2 == 1)
                .Select(et => "<strong>" + et.Tag + "</strong>: " + et.Value));     
            
            emailTags.Add(new EmailTag("[#formsautogenerate#]", autogeneratedForm));

            // Some additional tags as requested by by Matthew Kirschner (https://our.umbraco.org/projects/backoffice-extensions/perplexmail-for-umbraco/feedback/86764-access-record-ids-in-the-email-template)
            // A pull request was submitted, which was expanded at his request, see link to Our above.
            // We modified it somewhat as the proposed changes used record properties that are not yet
            // initialized here (.Id, .Created). We use .UniqueId and DateTime.Now instead.
            emailTags.AddRange(new[] 
            {
                new EmailTag("[#formName#]", record.GetForm()?.Name),
                new EmailTag("[#recordCreateDate#]", DateTime.Now.ToShortDateString()),
                new EmailTag("[#recordCreateTime#]", DateTime.Now.ToShortTimeString()),
                new EmailTag("[#recordCreateDateTime#]", DateTime.Now.ToShortDateString() + " " + DateTime.Now.ToShortTimeString()),
                // record.Id is not populated yet, presumably because another Workflow could still
                // prevent the record from being saved. However, record.UniqueId does already exist,
                // so we can pass that instead as a unique identifier. This UniqueId is also visible in 
                // the entry details in Umbraco.
                new EmailTag("[#recordId#]", record.UniqueId.ToString()),
            });   

            // A tag specifying whether or not attachments are sent with this email
            bool sendAttachments = false;
            bool.TryParse(Attachments, out sendAttachments);
            emailTags.Add(new EmailTag("[#attachments#]", sendAttachments));

            // TryParse is not necessary here, it is already validated in ValidateSettings
            int emailId = int.Parse(Email);

            List<RecordField> fileUploads = record.RecordFields.Values.Where(IsFileUpload).ToList();

            // If the form has any file upload fields and we should be attaching submitted
            // fields to the email, we will do it here.
            if (fileUploads.Any() && sendAttachments)
            {
                var attachments = new List<Attachment>();

                foreach (var fileUpload in fileUploads)
                {
                    // The file uploads are of type "multiple",
                    // so there could be multiple files uploaded for each upload.
                    // We will attach all of them.
                    foreach (var file in fileUpload.Values.Where(v => v != null).Select(v => v.ToString()))
                    {
                        var relativePath = file;

                        // PerplexMail seems to have some issues with a relative path,
                        // so we'll make it absolute.
                        try
                        {
                            var absolutePath = HostingEnvironment.MapPath(relativePath);
                            attachments.Add(new Attachment(absolutePath));
                        }
                        catch (Exception)
                        {
                            // Ignore a failed attachment and continue with the rest
                        }
                    }
                }

                PerplexMail.Email.SendUmbracoEmail(emailId, emailTags, attachments);
            }
            else
            {
                PerplexMail.Email.SendUmbracoEmail(emailId, emailTags);
            }

            return WorkflowExecutionStatus.Completed;
        }

        private bool IsFileUpload(RecordField r)
        {
            return
                r.Field.FieldTypeId == Guid_FileUpload ||
                r.Field.FieldTypeId == Guid_PerplexFileUpload ||
                r.Field.FieldTypeId == Guid_PerplexImageUpload;
        }

        /// <summary>
        /// Yields a list of EmailTags, two for the given Record.
        /// One will be [#field_alias#] => data, the other will be [#field_caption#] => data.
        /// Starting at Forms 4.4.3, field aliases are editable in the GUI, and as such are
        /// the best choice to act as the key. Earlier we used the caption of the field as the aliases were
        /// not visible in Umbraco at all. However, due to some obvious issues (duplicate captions for example),
        /// we prefer the alias. To keep backwards compatibility, we generate EmailTags with both keys (alias / caption).         
        /// </summary>
        /// <param name="recordField">Record field to generate EmailTags for</param>
        /// <returns></returns>
        private List<EmailTag> ParseRecordField(RecordField recordField)
        {
            // Parsing depends on RecordField Type.
            Guid fieldTypeId = recordField.Field.FieldTypeId;

            // The value of the EmailTag, parsed further below
            string value = recordField.Values.Count == 0 || recordField.Values[0] == null
                ? ""
                : recordField.Values[0].ToString();

            if (fieldTypeId == Guid_Checkbox)
            {
                bool boolValue = false;
                bool.TryParse(value, out boolValue);
                // EmailPackage deals with strings, and wants booleans as "true" or "false"
                value = boolValue.ToString().ToLower(); 
            }
            else if (fieldTypeId == Guid_TextArea)
            {
                // Replace newlines with HTML newlines
                value = value.Replace("\n", "<br/>");
            }
            else if (fieldTypeId == Guid_DatePicker)
            {               
                if (value != "")
                {
                    DateTime dateTime;
                    if (DateTime.TryParse(value, out dateTime))
                    {
                        value = dateTime.ToShortDateString();
                    }
                    else
                    {
                        value = "The DatePicker value could not be parsed into a DateTime instance. The DatePicker value was " + value;
                    }
                }
            }
            else if (IsFileUpload(recordField))
            {
                // Een FileUpload kan meerdere waardes hebben (voor elk geüpload bestand)
                // We gaan nog steeds 1 tag genereren, maar die kan dan dus meerdere linkjes
                // bevatten, 1 voor elk bestand.

                // Lijstje met <a href="...">'s
                List<string> links = recordField.Values.Select((filePath, idx) =>
                {
                    // filePath is een relatief pad, iets als "~/media/forms/upload/<GUID>/bestandsnaam.txt
                    string fileName = Path.GetFileName(filePath.ToString());

                    var httpContext = HttpContext.Current;
                    string host = ""; // Root van website
                    if (httpContext != null)
                    {
                        host = httpContext.Request.Url.GetLeftPart(UriPartial.Authority);
                    }
                    string url = host + filePath.ToString().Replace("~", "");

                    string link = "<a title=\"" + fileName + "\" href=\"" + url + "\">" + fileName + "</a>";

                    // Eerder hadden we losse tags voor Bestandsnaam en URL,
                    // maar in de praktijk zijn die niet zinnig om terug te geven
                    // want in de formsautogenerate toont hij alle tags + values.
                    // Daar willen we echter alleen maar een aanklikbare link en niet
                    // ook nog los bestandsnaam + url.
                    // Nu dus alleen nog een anchor tag met aanklikbare link
                    return link;
                }).ToList();

                // Indien het 1 bestand is leveren we 1 <a> tag op, verder niets.
                // Indien er meer dan 1 bestand is zetten we er enters tussen en doen we
                // ook een enter voor het eerste bestand zodat we een lijstje onder elkaar krijgen.
                if (links.Count == 1)
                {
                    value = links.First();
                }
                else
                {
                    value = "<br/>" + string.Join("<br/>", links);
                }
            }

            // Default case:
            // value becomes a comma-seperated string of the record values.
            // In case of a single value this obviously will be without a comma.
            else
            {
                value = string.Join(", ", recordField.Values.Where(v => v != null).Select(v => v.ToString()));
            }

            // Every tag value will lead to 2 EmailTags to maintain backwards compatibility;
            // 1) Tag == alias
            // 2) Tag == caption
            return new List<EmailTag> 
            {
                new EmailTag($"[#{recordField.Field.Alias}#]", value),
                new EmailTag($"[#{recordField.Field.Caption}#]", value)
            };
        }

        public override List<Exception> ValidateSettings()
        {
            var exceptions = new List<Exception>();
            int emailId = 0;
            if (!int.TryParse(Email, out emailId))
            {
                exceptions.Add(new Exception("No email node has been selected"));
            }

            return exceptions;
        }
    }
}