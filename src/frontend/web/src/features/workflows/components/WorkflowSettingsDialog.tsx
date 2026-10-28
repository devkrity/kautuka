import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { WorkflowChannelsPanel } from './WorkflowChannelsPanel';

export function WorkflowSettingsDialog({
  open,
  onClose,
}: Readonly<{ open: boolean; onClose: () => void }>) {
  return (
    <Dialog open={open} onOpenChange={(nextOpen) => !nextOpen && onClose()}>
      <DialogContent className="flex h-[85vh] w-[95vw] max-w-4xl flex-col overflow-hidden p-0">
        <DialogHeader className="border-b border-border/50 px-6 py-4 pr-10">
          <DialogTitle>Workflow settings</DialogTitle>
        </DialogHeader>
        <div className="min-h-0 flex-1 overflow-y-auto px-6 py-4">
          <Tabs defaultValue="channels" className="mt-1">
            <TabsList className="mb-3">
              <TabsTrigger value="channels">Channels</TabsTrigger>
            </TabsList>
            <TabsContent value="channels">
              <WorkflowChannelsPanel scope="shared" />
            </TabsContent>
          </Tabs>
        </div>
      </DialogContent>
    </Dialog>
  );
}
