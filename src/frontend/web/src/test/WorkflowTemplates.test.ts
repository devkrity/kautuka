import { describe, expect, it } from 'vitest';
import { buildWorkflowTemplate } from '@/features/workflows/model/workflowTemplates';

describe('workflow templates', () => {
  it('builds the email attachment template using the workflow scalar array schema', () => {
    const workflow = buildWorkflowTemplate('email-attachments', ['mail-agent', 'summary-agent']);

    expect(workflow.inputSchema).toMatchObject({
      required: ['search'],
      properties: { receivedAfter: { type: 'string' } },
    });
    expect(workflow.workingSchema).toMatchObject({
      properties: { items: { type: 'array', items: { type: 'string' } } },
    });
    expect(workflow.nodes.map((node) => node.agentId)).toEqual(['mail-agent', 'summary-agent']);
    expect(workflow.nodes[0]?.promptTemplate).toContain('{{receivedAfter}}');
    expect(workflow.nodes[0]?.promptTemplate).not.toMatch(/mail_list|mail_read|JSON object only/i);
  });

  it('uses pick-lists for fixed payroll and mortgage decisions', () => {
    const payroll = buildWorkflowTemplate('payroll', ['payroll-agent']);
    const mortgage = buildWorkflowTemplate('mortgage', ['mortgage-agent']);

    expect(
      (payroll.nodes[0]?.outputSchema as { properties: { recommendation: { enum: string[] } } })
        .properties.recommendation.enum
    ).toEqual(['lodge', 'hold']);
    expect(
      (mortgage.nodes[0]?.outputSchema as { properties: { decision: { enum: string[] } } })
        .properties.decision.enum
    ).toEqual(['proceed', 'refer', 'decline']);
  });

  it('gives every template schema field a plain title', () => {
    for (const templateId of ['payroll', 'mortgage', 'portfolio', 'psychologist', 'email-attachments']) {
      const workflow = buildWorkflowTemplate(templateId, ['agent']);
      const schemas = [workflow.inputSchema, workflow.workingSchema, workflow.outputSchema,
        ...workflow.nodes.map((node) => node.outputSchema)];
      for (const schema of schemas) {
        const properties = (schema as { properties?: Record<string, { title?: string }> }).properties ?? {};
        for (const [key, field] of Object.entries(properties)) {
          expect(field.title, `${templateId}.${key}`).toBeTruthy();
          expect(field.title).not.toBe(key);
        }
      }
    }
  });
});
