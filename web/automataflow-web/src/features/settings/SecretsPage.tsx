import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { unwrap } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Button, Card, EmptyState, ErrorState, Field, PageHeader, Spinner, Table, inputClass } from '../../components/ui';
import { formatDateTime } from '../../lib/format';

const NamePattern = /^[A-Za-z][A-Za-z0-9]{0,63}$/;

/** Workspace secrets (Admin+): names are listed, values are write-only and never shown again. */
export function SecretsPage() {
  const { api } = useApp();
  const queries = useQueryClient();
  const [name, setName] = useState('');
  const [value, setValue] = useState('');
  const [formError, setFormError] = useState<string | null>(null);
  const secrets = useQuery({ queryKey: ['secrets'], queryFn: () => unwrap(api.GET('/api/v1/secrets')) });

  const set = useMutation({
    mutationFn: () => unwrap(api.PUT('/api/v1/secrets/{name}', { params: { path: { name } }, body: { value } })),
    onSuccess: () => {
      setName('');
      setValue('');
      void queries.invalidateQueries({ queryKey: ['secrets'] });
    },
    onSettled: () => setValue(''), // never keep a secret value in memory longer than needed
  });
  const remove = useMutation({
    mutationFn: (secret: string) => unwrap(api.DELETE('/api/v1/secrets/{name}', { params: { path: { name: secret } } })),
    onSettled: () => void queries.invalidateQueries({ queryKey: ['secrets'] }),
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!NamePattern.test(name)) {
      setFormError('Use letters and digits, starting with a letter (at most 64).');
      return;
    }

    if (!value) {
      setFormError('Enter a value.');
      return;
    }

    setFormError(null);
    set.mutate();
  };

  return (
    <>
      <PageHeader title="Secrets" description="Credentials workflows use, such as a target system password. Values are encrypted and can never be read back." />
      <div className="grid gap-6 lg:grid-cols-3">
        <Card title="Stored secrets" className="lg:col-span-2">
          {secrets.isPending ? (
            <Spinner />
          ) : secrets.isError ? (
            <ErrorState error={secrets.error} />
          ) : secrets.data.length === 0 ? (
            <EmptyState title="No secrets" />
          ) : (
            <Table head={['Name', 'Updated', '']}>
              {secrets.data.map((s) => (
                <tr key={s.name}>
                  <td className="px-3 py-2 font-mono">{s.name}</td>
                  <td className="px-3 py-2 text-slate-500">{formatDateTime(s.updatedAt)}</td>
                  <td className="px-3 py-2 text-right">
                    <Button variant="ghost" disabled={remove.isPending} onClick={() => window.confirm(`Delete the secret ${s.name}? Workflows that use it will fail.`) && remove.mutate(s.name)}>
                      Delete
                    </Button>
                  </td>
                </tr>
              ))}
            </Table>
          )}
          {remove.isError && <ErrorState error={remove.error} />}
        </Card>
        <Card title="Set a secret">
          <form onSubmit={submit} className="space-y-4" noValidate>
            <Field label="Name" hint="Referenced in workflows as {{secret.name}}. Setting an existing name replaces its value.">
              <input className={`${inputClass} font-mono`} value={name} onChange={(e) => setName(e.target.value)} name="secretName" autoComplete="off" />
            </Field>
            <Field label="Value">
              <input type="password" className={inputClass} value={value} onChange={(e) => setValue(e.target.value)} name="secretValue" autoComplete="new-password" />
            </Field>
            {formError && <p className="text-sm text-rose-700">{formError}</p>}
            {set.isError && <ErrorState error={set.error} />}
            <Button type="submit" disabled={set.isPending}>
              Save secret
            </Button>
          </form>
        </Card>
      </div>
    </>
  );
}
