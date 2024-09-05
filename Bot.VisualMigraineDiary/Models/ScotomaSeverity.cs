using System;
using System.ComponentModel;
using System.Reflection;

namespace Bot.VisualMigraineDiary.Models
{
    public enum ScotomaSeverity
    {
        [Description("Very mild")]
        VeryMild = 1,
        [Description("Mild")]
        Mild,
        [Description("Moderate")]
        Moderate,
        [Description("Severe")]
        Severe,
        [Description("Complete vision loss")]
        CompleteVisionLoss
    }

    public static class ScotomaSeverityExtensions
    {
        public static string GetDescription(this ScotomaSeverity severity)
        {
            FieldInfo? field = severity.GetType().GetField(severity.ToString());
            if (field != null)
            {
                return field.GetCustomAttribute(typeof(DescriptionAttribute)) is not DescriptionAttribute attribute ? severity.ToString() : attribute.Description;
            }

            return severity.ToString();
        }
    }
}